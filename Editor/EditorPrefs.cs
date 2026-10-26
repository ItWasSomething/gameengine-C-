using System.Text.Json;

namespace Editor;

// Per-user editor settings that aren't part of any project: recently opened
// projects and the MyEngine Games sign-in. Like Unity's EditorPrefs, they
// live in the user profile (%APPDATA%\MyEngine\editor.json), not the repo —
// so an API token is never committed to git by accident.
public sealed class EditorPrefs
{
    public List<string> RecentProjects { get; set; } = new();

    // Where the MyEngine Games website/API runs. The default is the local
    // dev server (Website/Server); change it once the site is deployed.
    public string SiteUrl { get; set; } = "http://localhost:5080";
    public string? ApiToken { get; set; }
    public string? SiteUsername { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyEngine", "editor.json");

    private static EditorPrefs? _instance;
    public static EditorPrefs Instance => _instance ??= Load();

    private static EditorPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<EditorPrefs>(File.ReadAllText(FilePath)) ?? new EditorPrefs();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Engine.Log.Warn($"Couldn't read editor preferences ({e.Message}); starting fresh.");
        }
        return new EditorPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException e)
        {
            Engine.Log.Warn($"Couldn't save editor preferences: {e.Message}");
        }
    }

    // Most recent first, no duplicates, missing folders dropped, at most 10.
    public void AddRecentProject(string path)
    {
        path = Path.GetFullPath(path);
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(p));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        Save();
    }
}
