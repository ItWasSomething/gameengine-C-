using System.Text.Json;

namespace Editor.Build;

// Per-project build + store-page settings, saved in
// <project>/ProjectSettings/GameSettings.json so they're committed with the
// project (unlike EditorPrefs, which is per-user). Unity keeps the same kind
// of thing in Player Settings.
public sealed class GameSettings
{
    public string ProductName { get; set; } = "My Game";
    public string Version { get; set; } = "1.0";
    public string StartScene { get; set; } = "Assets/Scenes/Main.scene";
    // Self-contained builds include the .NET runtime, so players don't have
    // to install anything. Bigger download (~30 MB zipped), but it just runs.
    public bool SelfContained { get; set; } = true;

    // ---- Store page (used by Publish) ----
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    // Set after the first publish: later publishes upload a new *version* of
    // this game instead of creating a second game.
    public string? PublishedSlug { get; set; }

    public static string PathFor(string projectDir) => System.IO.Path.Combine(projectDir, "ProjectSettings", "GameSettings.json");

    public static GameSettings Load(string projectDir)
    {
        string file = PathFor(projectDir);
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(file)) ?? Default(projectDir);
        }
        catch (JsonException e)
        {
            Engine.Log.Warn($"ProjectSettings/GameSettings.json is invalid ({e.Message}); using defaults.");
        }
        return Default(projectDir);
    }

    public void Save(string projectDir)
    {
        string file = PathFor(projectDir);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static GameSettings Default(string projectDir) =>
        new() { ProductName = new DirectoryInfo(projectDir).Name };

    // "My Cool Game!" → "MyCoolGame" — safe as a file and exe name.
    public string SafeName
    {
        get
        {
            string s = string.Concat(ProductName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
            return s.Length > 0 ? s : "Game";
        }
    }
}
