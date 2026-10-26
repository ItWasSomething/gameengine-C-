using System.Runtime.CompilerServices;

namespace Engine.Core;

// Finds and loads project assets. Paths are project-relative with forward
// slashes, exactly like Unity's: "Assets/Prefabs/Coin.prefab". That keeps
// scene files portable — they never contain C:\Users\... paths.
public static class AssetDatabase
{
    // Loaded prefabs, remembered with the file time they were read at, so an
    // edited prefab is re-read automatically.
    private static readonly Dictionary<string, (GameObject Asset, DateTime WriteTime)> Prefabs = new();

    // Reverse lookup: asset object → its path. Weak, so dropping an asset
    // from the cache doesn't leak it through this table.
    private static readonly ConditionalWeakTable<GameObject, string> PathsByAsset = new();

    // The folder that contains Assets/ (and ProjectSettings/).
    public static string projectPath { get; set; } = Directory.GetCurrentDirectory();

    public static string assetsFolder => Path.Combine(projectPath, "Assets");

    public static string ToAbsolutePath(string assetPath) =>
        Path.GetFullPath(Path.Combine(projectPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));

    // C:\...\MyGame\Assets\Prefabs\Coin.prefab → "Assets/Prefabs/Coin.prefab"
    public static string ToAssetPath(string absolutePath) =>
        Path.GetRelativePath(projectPath, absolutePath).Replace('\\', '/');

    public static GameObject? LoadPrefab(string assetPath)
    {
        string file = ToAbsolutePath(assetPath);
        if (!File.Exists(file))
        {
            Debug.LogWarning($"Prefab not found: {assetPath}");
            return null;
        }

        var writeTime = File.GetLastWriteTimeUtc(file);
        if (Prefabs.TryGetValue(assetPath, out var cached) && cached.WriteTime == writeTime)
            return cached.Asset;

        try
        {
            var asset = SceneSerializer.ReadPrefab(File.ReadAllText(file));
            Prefabs[assetPath] = (asset, writeTime);
            PathsByAsset.AddOrUpdate(asset, assetPath);
            return asset;
        }
        catch (Exception e)
        {
            Debug.LogError($"Couldn't load prefab {assetPath}: {e.Message}");
            return null;
        }
    }

    public static string? GetAssetPath(GameObject asset) =>
        PathsByAsset.TryGetValue(asset, out var path) ? path : null;

    // Forget a cached asset (after it's been overwritten on disk).
    public static void Invalidate(string assetPath) => Prefabs.Remove(assetPath);

    // Forget every loaded prefab (after scripts recompile, cached prefabs
    // still hold components built from the old code).
    public static void ClearCache() => Prefabs.Clear();
}
