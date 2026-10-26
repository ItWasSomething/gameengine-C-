using System.Text.Json.Nodes;

namespace Engine.Core;

// Prefabs: a GameObject hierarchy saved as a reusable asset (.prefab file),
// stamped into scenes as "instances" that stay linked to the file.
//
// The linking rule here is deliberately simpler than Unity's full override
// system, and easy to state:
//
//   An instance keeps its own root name and root Transform (where you put
//   it). Everything else — components, values, children — comes from the
//   prefab file.
//
// So editing an instance and pressing "Apply" changes the prefab and every
// other instance; "Revert" throws your instance edits away; "Unpack" cuts
// the link and makes it an ordinary GameObject.
public static class PrefabUtility
{
    public static bool IsPrefabInstanceRoot(GameObject go) => go.prefabAssetPath != null;

    // The instance root this object belongs to, if any.
    public static GameObject? GetInstanceRoot(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (t.gameObject.prefabAssetPath != null) return t.gameObject;
        return null;
    }

    // Saves `go` as a new prefab and turns `go` into its first instance.
    public static void CreatePrefab(GameObject go, string assetPath)
    {
        go.prefabAssetPath = null;
        string file = AssetDatabase.ToAbsolutePath(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // Prefab files are stored "at the origin": the root's position and
        // rotation belong to each instance, not to the asset.
        var json = SceneSerializer.WritePrefab(go);
        ResetRootTransform(json["root"]!.AsObject());
        File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        AssetDatabase.Invalidate(assetPath);
        go.prefabAssetPath = assetPath;
    }

    // Writes an instance's current state back to its prefab file.
    public static void ApplyInstance(GameObject instanceRoot)
    {
        if (instanceRoot.prefabAssetPath is not { } path) return;
        CreatePrefab(instanceRoot, path);
    }

    public static void Unpack(GameObject instanceRoot) => instanceRoot.prefabAssetPath = null;

    // ---- Syncing instances (done on the JSON, before objects are built) --------------

    // Replaces every prefab instance in a scene document with fresh data from
    // its prefab file, keeping each instance's root name/Transform and its
    // object ids (so references from elsewhere in the scene still connect).
    // `filter` (instance root id, prefab path) limits which instances are
    // refreshed — e.g. just one for Revert.
    public static void SyncInstances(JsonObject sceneDocument, Func<Guid, string, bool>? filter = null)
    {
        if (sceneDocument["roots"] is not JsonArray roots) return;
        for (int i = 0; i < roots.Count; i++)
            if (roots[i] is JsonObject go && SyncRecursive(go, filter) is var synced && synced != go) roots[i] = synced;
    }

    private static JsonObject SyncRecursive(JsonObject go, Func<Guid, string, bool>? filter)
    {
        if (go["prefab"]?.GetValue<string>() is { } path)
        {
            Guid.TryParse(go["id"]?.GetValue<string>(), out var id);
            if ((filter == null || filter(id, path)) && LoadPrefabRootJson(path) is { } prefabRoot)
                return SyncInstance(go, prefabRoot, path);
            return go;
        }

        if (go["children"] is JsonArray children)
            for (int i = 0; i < children.Count; i++)
                if (children[i] is JsonObject child && SyncRecursive(child, filter) is var synced && synced != child) children[i] = synced;
        return go;
    }

    private static JsonObject? LoadPrefabRootJson(string assetPath)
    {
        string file = AssetDatabase.ToAbsolutePath(assetPath);
        if (!File.Exists(file))
        {
            Debug.LogWarning($"Prefab instance points to a missing file: {assetPath} (keeping its saved copy).");
            return null;
        }
        try { return JsonNode.Parse(File.ReadAllText(file))?["root"]?.AsObject(); }
        catch (Exception e)
        {
            Debug.LogError($"Couldn't read prefab {assetPath}: {e.Message}");
            return null;
        }
    }

    internal static JsonObject SyncInstance(JsonObject instance, JsonObject prefabRoot, string assetPath)
    {
        var fresh = (JsonObject)prefabRoot.DeepClone();

        // 1. Give every object in the fresh copy an id: the instance's id for
        //    the matching object (same path in the hierarchy), or a new one.
        //    Never the prefab file's own ids — two instances would collide.
        var instanceIndex = IndexByPath(instance);
        var idMap = new Dictionary<string, string>();   // prefab-file id → scene id
        AssignIds(fresh, "", instanceIndex, idMap);

        // 2. References between objects inside the prefab must follow the
        //    renamed ids.
        RemapReferences(fresh, idMap);

        // 3. The instance keeps its own root name, root Transform and link.
        fresh["name"] = instance["name"]?.DeepClone();
        fresh["active"] = instance["active"]?.DeepClone();
        fresh["prefab"] = assetPath;
        if (FindTransform(instance) is { } instTransform && FindTransform(fresh) is { } freshTransform)
            freshTransform["fields"] = instTransform["fields"]?.DeepClone();
        return fresh;
    }

    // Path keys like "/Body#0/Arm#0" (name + index among same-named
    // siblings), mapped to that GameObject's JSON.
    private static Dictionary<string, JsonObject> IndexByPath(JsonObject root)
    {
        var index = new Dictionary<string, JsonObject>();
        Walk(root, "");
        return index;

        void Walk(JsonObject go, string path)
        {
            index[path] = go;
            foreach (var (child, key) in ChildKeys(go))
                Walk(child, path + "/" + key);
        }
    }

    private static IEnumerable<(JsonObject Child, string Key)> ChildKeys(JsonObject go)
    {
        if (go["children"] is not JsonArray children) yield break;
        var seen = new Dictionary<string, int>();
        foreach (var child in children.OfType<JsonObject>())
        {
            string name = child["name"]?.GetValue<string>() ?? "";
            int n = seen.GetValueOrDefault(name);
            seen[name] = n + 1;
            yield return (child, $"{name}#{n}");
        }
    }

    private static void AssignIds(JsonObject fresh, string path, Dictionary<string, JsonObject> instanceIndex,
        Dictionary<string, string> idMap)
    {
        instanceIndex.TryGetValue(path, out var match);
        MapId(fresh, match?["id"]);

        // Components match by type + occurrence ("the 2nd Light").
        if (fresh["components"] is JsonArray freshComps)
        {
            var matchComps = (match?["components"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            var counts = new Dictionary<string, int>();
            foreach (var comp in freshComps.OfType<JsonObject>())
            {
                string type = comp["type"]?.GetValue<string>() ?? "";
                int n = counts.GetValueOrDefault(type);
                counts[type] = n + 1;
                var twin = matchComps.Where(c => c["type"]?.GetValue<string>() == type).ElementAtOrDefault(n);
                MapId(comp, twin?["id"]);
            }
        }

        foreach (var (child, key) in ChildKeys(fresh))
            AssignIds(child, path + "/" + key, instanceIndex, idMap);

        void MapId(JsonObject obj, JsonNode? keep)
        {
            string oldId = obj["id"]?.GetValue<string>() ?? "";
            string newId = keep?.GetValue<string>() ?? Guid.NewGuid().ToString();
            if (oldId.Length > 0) idMap[oldId] = newId;
            obj["id"] = newId;
        }
    }

    private static void RemapReferences(JsonNode? node, Dictionary<string, string> idMap)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 1 && obj["ref"]?.GetValue<string>() is { } id && idMap.TryGetValue(id, out var mapped))
                {
                    obj["ref"] = mapped;
                    return;
                }
                foreach (var (_, value) in obj.ToList()) RemapReferences(value, idMap);
                break;
            case JsonArray array:
                foreach (var item in array) RemapReferences(item, idMap);
                break;
        }
    }

    private static JsonObject? FindTransform(JsonObject go) =>
        (go["components"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["type"]?.GetValue<string>() == nameof(Transform));

    private static void ResetRootTransform(JsonObject root)
    {
        if (FindTransform(root)?["fields"] is not JsonObject fields) return;
        fields["localPosition"] = new JsonArray(0f, 0f, 0f);
        fields["localEulerAngles"] = new JsonArray(0f, 0f, 0f);
    }
}
