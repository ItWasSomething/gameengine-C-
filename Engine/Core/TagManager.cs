using System.Text.Json;

namespace Engine.Core;

// Project-wide tag and layer names (Unity's Project Settings > Tags and
// Layers), stored in <project>/ProjectSettings/TagManager.json.
//
// Tags are free-form labels for *finding* things (FindWithTag("Player")).
// Layers are numbered 0..31 for *filtering* things — which layers a camera
// renders or which collide — because 32 on/off flags fit in one int
// (LayerMask).
public static class TagManager
{
    public const string Untagged = "Untagged";
    public const string MainCamera = "MainCamera";
    public const string Player = "Player";

    public static readonly string[] BuiltinTags =
        { Untagged, "Respawn", "Finish", "EditorOnly", MainCamera, Player, "GameController" };

    public const int LayerCount = 32;

    // Layers 0-5 are built in (as in Unity); 6-31 are yours to name.
    private static readonly string[] BuiltinLayers = { "Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI" };

    public static List<string> customTags { get; } = new();
    public static string[] layers { get; } = CreateDefaultLayers();

    public static IEnumerable<string> allTags => BuiltinTags.Concat(customTags);

    private static string[] CreateDefaultLayers()
    {
        var l = new string[LayerCount];
        for (int i = 0; i < LayerCount; i++) l[i] = i < BuiltinLayers.Length ? BuiltinLayers[i] : "";
        return l;
    }

    public static bool IsBuiltinLayer(int index) => index < BuiltinLayers.Length;

    public static void AddTag(string tag)
    {
        tag = tag.Trim();
        if (tag.Length > 0 && !allTags.Contains(tag)) customTags.Add(tag);
    }

    public static int NameToLayer(string layerName) => Array.IndexOf(layers, layerName);
    public static string LayerToName(int layer) => layer is >= 0 and < LayerCount ? layers[layer] : "";

    // ---- Persistence ------------------------------------------------------------

    private sealed record Data(List<string> Tags, string[] Layers);

    public static void Load(string projectDirectory)
    {
        string path = SettingsPath(projectDirectory);
        customTags.Clear();
        var defaults = CreateDefaultLayers();
        Array.Copy(defaults, layers, LayerCount);
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
            if (data == null) return;
            customTags.AddRange(data.Tags.Where(t => !BuiltinTags.Contains(t)));
            for (int i = BuiltinLayers.Length; i < LayerCount && i < data.Layers.Length; i++) layers[i] = data.Layers[i];
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Couldn't read {path}: {e.Message}");
        }
    }

    public static void Save(string projectDirectory)
    {
        string path = SettingsPath(projectDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Data(customTags, layers), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SettingsPath(string projectDirectory) =>
        Path.Combine(projectDirectory, "ProjectSettings", "TagManager.json");
}

// A set of layers as a bitmask: bit N set = layer N included.
[Serializable]
public struct LayerMask
{
    public int value;

    public LayerMask(int value) => this.value = value;

    public static implicit operator int(LayerMask mask) => mask.value;
    public static implicit operator LayerMask(int value) => new(value);

    public bool Contains(int layer) => (value & (1 << layer)) != 0;

    public static int GetMask(params string[] layerNames)
    {
        int mask = 0;
        foreach (var n in layerNames)
        {
            int layer = TagManager.NameToLayer(n);
            if (layer >= 0) mask |= 1 << layer;
        }
        return mask;
    }

    public static int NameToLayer(string name) => TagManager.NameToLayer(name);
    public static string LayerToName(int layer) => TagManager.LayerToName(layer);

    public static LayerMask Everything => new(~0);
}
