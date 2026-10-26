using Engine;
using Engine.Fps;
using Engine.Runtime;

// `dotnet run --project Game/Game.csproj -- fps` launches the first-person
// shooter mode; `-- scene <file.scene>` plays a scene made in the editor;
// plain `dotnet run` keeps launching the platformer.
if (args.Any(a => a.Equals("fps", StringComparison.OrdinalIgnoreCase)))
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
