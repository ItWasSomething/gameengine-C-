using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Engine.Core;

// Saves and loads scenes and prefabs as human-readable JSON:
//
//   { "format": 2, "kind": "scene", "name": "Main",
//     "roots": [
//       { "name": "Player", "id": "…", "active": true, "tag": "Player", "layer": 0,
//         "components": [
//           { "type": "Transform", "id": "…", "fields": { "localPosition": [0, 1, 0], … } },
//           { "type": "PlayerController", "id": "…", "enabled": true,
//             "fields": { "speed": 5, "target": { "ref": "<id of another object>" } } } ],
//         "children": [ … ] } ] }
//
// References between objects are stored by id and connected in a second
// pass once everything exists ("fixups") — an object can point at one that
// comes later in the file. Prefab references are stored by asset path.
//
// The same code is the engine's deep copy: Instantiate, Duplicate, Play-mode
// snapshots and undo all go through it.
public static class SceneSerializer
{
    public const int FormatVersion = 2;
    public const string SceneExtension = ".scene";
    public const string PrefabExtension = ".prefab";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---- Scenes ------------------------------------------------------------------

    public static JsonObject WriteScene(Scene scene) => new()
    {
        ["format"] = FormatVersion,
        ["kind"] = "scene",
        ["name"] = scene.name,
        ["roots"] = new JsonArray(scene.GetRootGameObjects().Select(go => (JsonNode)WriteGameObject(go)).ToArray()),
    };

    public static string SceneToJson(Scene scene) => WriteScene(scene).ToJsonString(Indented);

    public static void SaveScene(Scene scene, string path)
    {
        File.WriteAllText(path, SceneToJson(scene));
        scene.path = path;
        scene.name = Path.GetFileNameWithoutExtension(path);
    }

    public static Scene LoadScene(string path)
    {
        var scene = SceneFromJson(File.ReadAllText(path), syncPrefabs: true);
        scene.path = path;
        scene.name = Path.GetFileNameWithoutExtension(path);
        return scene;
    }

    // syncPrefabs: refresh prefab instances from their .prefab files first
    // (on open). Play-mode snapshots and undo skip it — they must restore
    // exactly what was there.
    public static Scene SceneFromJson(string json, bool syncPrefabs = false)
    {
        var doc = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty scene file.");
        int format = doc["format"]?.GetValue<int>() ?? 0;
        if (format != FormatVersion)
            throw new InvalidDataException($"Scene format {format} isn't supported (expected {FormatVersion}).");
        if (syncPrefabs) PrefabUtility.SyncInstances(doc);

        var scene = new Scene { name = doc["name"]?.GetValue<string>() ?? "Untitled" };
        var ctx = new ReadContext();
        if (doc["roots"] is JsonArray roots)
            foreach (var node in roots.OfType<JsonObject>())
                scene.AddRootInternal(ReadGameObject(node, ctx));
        ctx.Resolve();
        return scene;
    }

    // ---- Prefabs -----------------------------------------------------------------

    public static JsonObject WritePrefab(GameObject root)
    {
        var json = WriteGameObject(root);
        json.Remove("prefab");    // a prefab isn't an instance of itself
        return new JsonObject { ["format"] = FormatVersion, ["kind"] = "prefab", ["root"] = json };
    }

    public static void SavePrefab(GameObject root, string path) =>
        File.WriteAllText(path, WritePrefab(root).ToJsonString(Indented));

    // Loads a prefab as a standalone GameObject that belongs to no scene —
    // an "asset". Instantiate it to get a live copy.
    public static GameObject ReadPrefab(string json)
    {
        var doc = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty prefab file.");
        var rootJson = doc["root"] as JsonObject ?? throw new InvalidDataException("Prefab has no root.");
        var ctx = new ReadContext();
        var root = ReadGameObject(rootJson, ctx);
        ctx.Resolve();
        return root;
    }

    // ---- Cloning (Instantiate / Duplicate) -----------------------------------------

    // A deep copy with brand-new ids. References inside the copied hierarchy
    // point at the copies; references outside it keep pointing at the
    // original targets (found in the source's scene).
    public static GameObject CloneGameObject(GameObject source)
    {
        var json = WriteGameObject(source);
        var scene = source.scene;
        var ctx = new ReadContext { External = id => scene?.FindById(id) };
        var clone = ReadGameObject(json, ctx);
        ctx.Resolve();
        foreach (var obj in ctx.Created) obj.instanceId = Guid.NewGuid();
        return clone;
    }

    // ---- GameObjects -------------------------------------------------------------

    // The hierarchy currently being written. References into it are always
    // saved by id, even when it's a prefab asset (a prefab's field pointing
    // at its own child must stay pointing at the *copy's* child).
    [ThreadStatic] private static GameObject? _writeRoot;

    public static JsonObject WriteGameObject(GameObject go)
    {
        bool outermost = _writeRoot == null;
        if (outermost) _writeRoot = go;
        try { return WriteGameObjectInner(go); }
        finally { if (outermost) _writeRoot = null; }
    }

    private static JsonObject WriteGameObjectInner(GameObject go)
    {
        var obj = new JsonObject
        {
            ["name"] = go.name,
            ["id"] = go.instanceId.ToString(),
            ["active"] = go.activeSelf,
            ["tag"] = go.tag,
            ["layer"] = go.layer,
        };
        if (go.prefabAssetPath != null) obj["prefab"] = go.prefabAssetPath;
        obj["components"] = new JsonArray(go.components.Select(c => (JsonNode)WriteComponent(c)).ToArray());
        if (go.transform.childCount > 0)
            obj["children"] = new JsonArray(go.transform.Select(t => (JsonNode)WriteGameObject(t.gameObject)).ToArray());
        return obj;
    }

    internal static JsonObject WriteComponent(Component c)
    {
        if (c is MissingComponent missing && missing.originalJson != null)
        {
            var copy = (JsonObject)missing.originalJson.DeepClone();
            copy["id"] = c.instanceId.ToString();
            return copy;
        }

        var obj = new JsonObject { ["type"] = c.GetType().Name, ["id"] = c.instanceId.ToString() };
        if (c is Behaviour b) obj["enabled"] = b.enabled;
        obj["fields"] = c is Transform t ? WriteTransform(t) : WriteFields(c);
        return obj;
    }

    private static JsonObject WriteTransform(Transform t) => new()
    {
        ["localPosition"] = Vec(t.localPosition),
        ["localEulerAngles"] = Vec(t.localEulerAngles),
        ["localScale"] = Vec(t.localScale),
    };

    internal static GameObject ReadGameObject(JsonObject json, ReadContext ctx)
    {
        var go = new GameObject(json["name"]?.GetValue<string>() ?? "GameObject", addToActiveScene: false);
        ctx.Register(json["id"], go);
        go.SetActive(json["active"]?.GetValue<bool>() ?? true);
        go.tag = json["tag"]?.GetValue<string>() ?? TagManager.Untagged;
        go.layer = json["layer"]?.GetValue<int>() ?? 0;
        go.prefabAssetPath = json["prefab"]?.GetValue<string>();

        if (json["components"] is JsonArray components)
            foreach (var cjson in components.OfType<JsonObject>())
                ReadComponent(go, cjson, ctx);

        if (json["children"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                ReadGameObject(child, ctx).transform.SetParent(go.transform, worldPositionStays: false);

        return go;
    }

    private static void ReadComponent(GameObject go, JsonObject json, ReadContext ctx)
    {
        string typeName = json["type"]?.GetValue<string>() ?? "";
        Component component;
        if (typeName == nameof(Transform))
        {
            component = go.transform;
            if (json["fields"] is JsonObject tf) ReadTransform(go.transform, tf);
        }
        else if (TypeRegistry.FindComponentType(typeName) is { } type)
        {
            component = (Component)Activator.CreateInstance(type)!;
            go.AttachComponent(component);
            if (json["fields"] is JsonObject fields) ReadFields(component, fields, ctx);
        }
        else
        {
            Debug.LogWarning($"'{go.name}' references a missing script '{typeName}'. Its data is kept.");
            component = new MissingComponent { scriptName = typeName, originalJson = (JsonObject)json.DeepClone() };
            go.AttachComponent(component);
        }

        ctx.Register(json["id"], component);
        if (component is Behaviour b) b.SetEnabledSilently(json["enabled"]?.GetValue<bool>() ?? true);
    }

    private static void ReadTransform(Transform t, JsonObject f)
    {
        if (f["localPosition"] is JsonArray p) t.localPosition = ToVec3(p);
        if (f["localEulerAngles"] is JsonArray e) t.localEulerAngles = ToVec3(e);
        if (f["localScale"] is JsonArray s) t.localScale = ToVec3(s);
    }

    // ---- Fields --------------------------------------------------------------------

    public static JsonObject WriteFields(object target)
    {
        var obj = new JsonObject();
        foreach (var field in TypeRegistry.GetSerializedFields(target.GetType()))
            obj[field.Name] = WriteValue(field.GetValue(target), field.Type);
        return obj;
    }

    internal static void ReadFields(object target, JsonObject json, ReadContext ctx)
    {
        foreach (var field in TypeRegistry.GetSerializedFields(target.GetType()))
        {
            if (!json.TryGetPropertyValue(field.Name, out var node)) continue;
            try
            {
                var value = ReadValue(node, field.Type, field.GetValue(target), ctx, v => field.SetValue(target, v));
                field.SetValue(target, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Couldn't read {target.GetType().Name}.{field.Name}: {e.Message}");
            }
        }
    }

    public static JsonNode? WriteValue(object? value, Type type)
    {
        if (typeof(EngineObject).IsAssignableFrom(type)) return WriteReference(value as EngineObject);
        switch (value)
        {
            case null: return null;
            case float f: return f;
            case double d: return d;
            case int i: return i;
            case long l: return l;
            case bool b: return b;
            case string s: return s;
            case Enum e: return e.ToString();
            case Vector2 v: return new JsonArray(v.X, v.Y);
            case Vector3 v: return Vec(v);
            case Vector4 v: return new JsonArray(v.X, v.Y, v.Z, v.W);
            case Quaternion q: return new JsonArray(q.X, q.Y, q.Z, q.W);
            case Color c: return new JsonArray(c.r, c.g, c.b, c.a);
            case IList list:
                var element = TypeRegistry.ElementType(type);
                return new JsonArray(list.Cast<object?>().Select(item => WriteValue(item, element)).ToArray());
        }
        if (value.GetType().IsPrimitive) return JsonValue.Create(Convert.ToDouble(value));
        return WriteFields(value);   // a [Serializable] class or struct
    }

    // Scene objects are referenced by id; prefab assets by path.
    private static JsonNode? WriteReference(EngineObject? obj)
    {
        if (!obj) return null;
        var go = obj switch { GameObject g => g, Component c => c.gameObject, _ => null };
        bool insideWritten = go != null && _writeRoot != null && go.transform.IsChildOf(_writeRoot.transform);
        if (go != null && !insideWritten && go.scene == null &&
            AssetDatabase.GetAssetPath(go.transform.root.gameObject) is { } assetPath)
        {
            var reference = new JsonObject { ["asset"] = assetPath };
            if (obj is Component component) reference["componentType"] = component.GetType().Name;
            return reference;
        }
        return new JsonObject { ["ref"] = obj!.instanceId.ToString() };
    }

    // `assignLater` is how references get filled in after everything loads.
    internal static object? ReadValue(JsonNode? node, Type type, object? existing, ReadContext ctx, Action<object?> assignLater)
    {
        if (node == null) return TypeRegistry.DefaultValue(type);

        if (typeof(EngineObject).IsAssignableFrom(type))
        {
            if (node["ref"]?.GetValue<string>() is { } id && Guid.TryParse(id, out var guid))
                ctx.Fixups.Add((guid, type, assignLater));
            else if (node["asset"]?.GetValue<string>() is { } assetPath)
            {
                var asset = AssetDatabase.LoadPrefab(assetPath);
                return type == typeof(GameObject) ? asset : asset?.GetComponent(type);
            }
            return null;
        }

        if (type == typeof(float)) return node.GetValue<float>();
        if (type == typeof(double)) return node.GetValue<double>();
        if (type == typeof(int)) return node.GetValue<int>();
        if (type == typeof(long)) return node.GetValue<long>();
        if (type == typeof(bool)) return node.GetValue<bool>();
        if (type == typeof(string)) return node.GetValue<string>();
        if (type.IsEnum) return Enum.Parse(type, node.GetValue<string>(), ignoreCase: true);
        if (type.IsPrimitive) return Convert.ChangeType(node.GetValue<double>(), type);

        if (node is JsonArray a)
        {
            float F(int i) => a.Count > i ? a[i]!.GetValue<float>() : 0f;
            if (type == typeof(Vector2)) return new Vector2(F(0), F(1));
            if (type == typeof(Vector3)) return new Vector3(F(0), F(1), F(2));
            if (type == typeof(Vector4)) return new Vector4(F(0), F(1), F(2), F(3));
            if (type == typeof(Quaternion)) return new Quaternion(F(0), F(1), F(2), F(3));
            if (type == typeof(Color)) return new Color(F(0), F(1), F(2), a.Count > 3 ? F(3) : 1f);

            if (type.IsArray || TypeRegistry.IsList(type, out _))
            {
                var element = TypeRegistry.ElementType(type);
                var collection = TypeRegistry.CreateCollection(type, a.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    int index = i;
                    collection[i] = ReadValue(a[i], element, null, ctx, v => collection[index] = v);
                }
                return collection;
            }
        }

        if (node is JsonObject obj)
        {
            var instance = existing ?? Activator.CreateInstance(type)!;
            ReadFields(instance, obj, ctx);
            return instance;
        }

        throw new InvalidDataException($"Can't read {type.Name} from {node.ToJsonString()}");
    }

    private static JsonArray Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 ToVec3(JsonArray a) =>
        new(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>());
}

// State for one load: every object created so far (by the id in the file),
// and the references waiting to be connected once all objects exist.
internal sealed class ReadContext
{
    public readonly Dictionary<Guid, EngineObject> ById = new();
    public readonly List<EngineObject> Created = new();
    public readonly List<(Guid Id, Type Type, Action<object?> Assign)> Fixups = new();

    // Fallback for references that point outside what's being loaded (used
    // by Instantiate: a copied enemy still targets the original player).
    public Func<Guid, EngineObject?>? External;

    public void Register(JsonNode? idNode, EngineObject obj)
    {
        Created.Add(obj);
        if (Guid.TryParse(idNode?.GetValue<string>(), out var id))
        {
            obj.instanceId = id;
            ById[id] = obj;
        }
    }

    public void Resolve()
    {
        foreach (var (id, type, assign) in Fixups)
        {
            var target = ById.GetValueOrDefault(id) ?? External?.Invoke(id);
            // A field typed as a component can be filled from a GameObject
            // reference and vice versa, as long as the types line up.
            object? value = target switch
            {
                null => null,
                _ when type.IsInstanceOfType(target) => target,
                GameObject go when typeof(Component).IsAssignableFrom(type) => go.GetComponent(type),
                Component c when type == typeof(GameObject) => c.gameObject,
                _ => null,
            };
            assign(value);
        }
        Fixups.Clear();
    }
}
