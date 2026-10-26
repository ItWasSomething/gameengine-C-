using Editor;

// dotnet run --project Editor [-- <projectDir>] [--screenshot <file.png>] [--frames N] [--play]
//
// With no project directory the Project Hub opens (recent projects, New
// Project, and the bundled SampleProject). The test flags (--screenshot,
// --play, --select, --script, --window build/publish/shortcuts) without a project open SampleProject directly,
// so automated runs don't stop at the hub. --hub forces the hub (for
// screenshotting it).

// If the editor ever crashes, keep the evidence: the full exception goes to
// %APPDATA%\MyEngine\crash.log (the console window often closes with it).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyEngine");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "crash.log");
        File.AppendAllText(log, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{e.ExceptionObject}\n\n");
        Console.Error.WriteLine($"MyEngine crashed. Details saved to {log}");
    }
    catch { /* never throw from the crash handler */ }
};

string? project = null;
string? screenshot = null;
int frames = 90;
bool play = false;
bool hub = false;
string? select = null;
string? script = null;
string? window = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--screenshot" when i + 1 < args.Length: screenshot = Path.GetFullPath(args[++i]); break;
        case "--frames" when i + 1 < args.Length: frames = int.Parse(args[++i]); break;
        case "--play": play = true; break;
        case "--hub": hub = true; break;
        case "--window" when i + 1 < args.Length: window = args[++i]; break;   // build | publish | shortcuts
        case "--select" when i + 1 < args.Length: select = args[++i]; break;
        case "--script" when i + 1 < args.Length: script = Path.GetFullPath(args[++i]); break;
        default: project = Path.GetFullPath(args[i]); break;
    }
}

string sample = FindSampleProject();
bool automated = screenshot != null || play || select != null || script != null || window != null;
if (project == null && automated && !hub) project = sample;
new EditorApp(new EditorOptions(project, screenshot, frames, play, select, script, sample, window)).Run();

static string FindSampleProject()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "MyEngine.slnx")))
            return Path.Combine(dir.FullName, "SampleProject");
    return Path.Combine(Directory.GetCurrentDirectory(), "SampleProject");
}
