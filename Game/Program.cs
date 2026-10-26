using System.Text.Json;
using Engine;
using Engine.Fps;
using Engine.Runtime;

// A built game (File → Build Game in the editor) ships this exe next to a
// game.json + Data/ folder; if that's there, play the game it describes.
//
// Otherwise (development):
//   `dotnet run --project Game/Game.csproj -- fps` launches the first-person
//   shooter mode; `-- scene <file.scene>` plays a scene made in the editor;
//   plain `dotnet run` keeps launching the platformer.
string gameJson = Path.Combine(AppContext.BaseDirectory, "game.json");
if (args.Length == 0 && File.Exists(gameJson))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(gameJson));
    var root = doc.RootElement;
    string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Game" : "Game";
    string scene = root.GetProperty("startScene").GetString()!;
    string scenePath = Path.Combine(AppContext.BaseDirectory, "Data", scene.Replace('/', Path.DirectorySeparatorChar));
    new SceneRunner(scenePath, name).Run();
}
else if (args.Any(a => a.Equals("fps", StringComparison.OrdinalIgnoreCase)))
{
    new FpsWindow("My Engine - FPS").Run();
}
else if (args.Length >= 2 && args[0].Equals("scene", StringComparison.OrdinalIgnoreCase))
{
    new SceneRunner(Path.GetFullPath(args[1])).Run();
}
else
{
    var game = new GameWindow("My Engine - Platformer Prototype");
    game.Run();
}
