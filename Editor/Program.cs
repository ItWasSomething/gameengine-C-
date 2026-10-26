using Editor;

// dotnet run --project Editor [-- <projectDir>] [--screenshot <file.png>] [--frames N] [--play]
//
// With no project directory, opens SampleProject/ at the repository root
// (found by walking up from the executable until MyEngine.slnx appears).

string? project = null;
string? screenshot = null;
int frames = 90;
bool play = false;
string? select = null;
string? script = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--screenshot" when i + 1 < args.Length: screenshot = Path.GetFullPath(args[++i]); break;
        case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i]); break;
        case "--play": play = true; break;
        case "--select" when i + 1 < args.Length: select = args[++i]; break;
        case "--script" when i + 1 < args.Length: script = Path.GetFullPath(args[++i]); break;
        default: project = Path.GetFullPath(args[i]); break;
    }
}

project ??= FindDefaultProject();
new EditorApp(new EditorOptions(project, screenshot, frames, play, select, script)).Run();

static string FindDefaultProject()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "MyEngine.slnx")))
            return Path.Combine(dir.FullName, "SampleProject");
    return Path.Combine(Directory.GetCurrentDirectory(), "SampleProject");
}
