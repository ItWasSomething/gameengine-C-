using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Engine.Core;

namespace Editor.Build;

// File → Build Game: turns a project into a .zip anyone can unzip and play.
//
//   1. `dotnet publish` the Game project (the same player you get from
//      `dotnet run --project Game -- scene x.scene`) for Windows.
//   2. Copy the project's data next to it:      MyGame/
//        Data/Assets/...            scenes, prefabs     ├─ MyGame.exe
//        Data/ProjectSettings/...   tags, layers        ├─ (runtime + engine dlls)
//        Data/Library/ScriptAssemblies/  compiled       ├─ game.json
//                                   scripts             └─ Data/...
//   3. Write game.json, which tells the player which scene to start with —
//      Game/Program.cs looks for it on launch.
//   4. Zip the folder into <project>/Builds/.
//
// It runs on a background task; the UI polls Status/Step/Log every frame.
public sealed class GameBuilder
{
    public enum BuildStatus { Idle, Running, Succeeded, Failed }

    public BuildStatus Status { get; private set; } = BuildStatus.Idle;
    public string Step { get; private set; } = "";
    public float Progress { get; private set; }
    public string? ZipPath { get; private set; }
    public string? Error { get; private set; }
    public ConcurrentQueue<string> Log { get; } = new();
    public bool IsRunning => Status == BuildStatus.Running;

    private CancellationTokenSource? _cancel;

    public void Cancel() => _cancel?.Cancel();

    // Everything the build needs is passed in (no EditorContext), so it can't
    // touch the live scene from the background thread.
    public Task<string?> BuildAsync(string projectDir, GameSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("A build is already running.");
        Status = BuildStatus.Running;
        Error = null;
        ZipPath = null;
        Log.Clear();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        return Task.Run(async () =>
        {
            try
            {
                string zip = await RunAsync(projectDir, settings, token);
                ZipPath = zip;
                Status = BuildStatus.Succeeded;
                Report("Done", 1f);
                AddLog($"Build finished: {zip}");
                return (string?)zip;
            }
            catch (OperationCanceledException)
            {
                Fail("Build cancelled.");
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
            return null;
        });
    }

    private void Fail(string message)
    {
        Error = message;
        Status = BuildStatus.Failed;
        AddLog("ERROR: " + message);
    }

    private async Task<string> RunAsync(string projectDir, GameSettings s, CancellationToken token)
    {
        // ---- Check ----------------------------------------------------------------
        Report("Checking project", 0.02f);
        string startScene = Path.Combine(projectDir, s.StartScene.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(startScene))
            throw new InvalidOperationException($"Start scene \"{s.StartScene}\" doesn't exist. Pick one in Build Settings.");
        string gameProject = FindGameProject(projectDir)
            ?? throw new InvalidOperationException("Can't find Game/Game.csproj. Building needs the MyEngine source folder (the one with MyEngine.slnx) and the .NET SDK.");

        // ---- 1. Publish the player ----------------------------------------------------
        string playerDir = Path.Combine(projectDir, "Library", "PlayerBuild");
        if (Directory.Exists(playerDir)) Directory.Delete(playerDir, recursive: true);
        Report("Compiling the player (first build takes a minute)", 0.08f);
        var args = new List<string>
        {
            "publish", gameProject, "-c", "Release", "-r", "win-x64",
            "--self-contained", s.SelfContained ? "true" : "false",
            "-o", playerDir, "-nologo", "-p:DebugType=None",
        };
        int exit = await RunProcessAsync("dotnet", args, token);
        if (exit != 0) throw new InvalidOperationException($"dotnet publish failed (exit code {exit}) — see the build log.");

        // ---- 2. Stage the build folder ----------------------------------------------------
        Report("Copying game data", 0.7f);
        string buildsDir = Path.Combine(projectDir, "Builds");
        string stage = Path.Combine(buildsDir, s.SafeName);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        CopyDirectory(playerDir, stage, _ => true);

        // The player is "Game.exe"; rename it after the game. Only the .exe is
        // renamed — it still loads Game.dll, whose name is baked into it.
        string exe = Path.Combine(stage, "Game.exe");
        if (File.Exists(exe)) File.Move(exe, Path.Combine(stage, s.SafeName + ".exe"), overwrite: true);

        string data = Path.Combine(stage, "Data");
        CopyDirectory(Path.Combine(projectDir, "Assets"), Path.Combine(data, "Assets"),
            f => !f.EndsWith(".recovery" + SceneSerializer.SceneExtension, StringComparison.OrdinalIgnoreCase));
        CopyDirectory(Path.Combine(projectDir, "ProjectSettings"), Path.Combine(data, "ProjectSettings"), _ => true);
        CopyDirectory(Path.Combine(projectDir, "Library", "ScriptAssemblies"), Path.Combine(data, "Library", "ScriptAssemblies"), _ => true);
        token.ThrowIfCancellationRequested();

        // ---- 3. game.json + a readme ------------------------------------------------------------
        File.WriteAllText(Path.Combine(stage, "game.json"), JsonSerializer.Serialize(new
        {
            name = s.ProductName,
            version = s.Version,
            startScene = s.StartScene,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(stage, "README.txt"),
            $"{s.ProductName} {s.Version}\r\n\r\nRun {s.SafeName}.exe to play. Esc quits.\r\n" +
            (s.SelfContained ? "" : "Needs the .NET 10 Desktop Runtime: https://dotnet.microsoft.com/download\r\n") +
            "\r\nMade with MyEngine.\r\n");

        // ---- 4. Zip ------------------------------------------------------------------------------
        Report("Compressing", 0.85f);
        string zip = Path.Combine(buildsDir, $"{s.SafeName}-{SafeFilePart(s.Version)}-win64.zip");
        if (File.Exists(zip)) File.Delete(zip);
        // includeBaseDirectory: the zip contains a "MyGame/" folder, so
        // unzipping doesn't spray files all over the player's Downloads.
        ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        AddLog($"Zip: {new FileInfo(zip).Length / 1024 / 1024.0:0.0} MB");
        return zip;
    }

    // Walk up from the editor's exe (then from the project) until
    // MyEngine.slnx appears — the same way Editor/Program.cs finds SampleProject.
    public static string? FindGameProject(string? projectDir = null)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, projectDir })
        {
            if (start == null) continue;
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Game", "Game.csproj");
                if (File.Exists(Path.Combine(dir.FullName, "MyEngine.slnx")) && File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private async Task<int> RunProcessAsync(string file, List<string> args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        AddLog("> " + file + " " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Couldn't run `dotnet`. Install the .NET 10 SDK and make sure it's on PATH.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Creep the progress bar forward while publish runs (it gives no
        // percentage), so the UI doesn't look frozen.
        while (!process.HasExited)
        {
            if (token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                token.ThrowIfCancellationRequested();
            }
            Progress = MathF.Min(0.65f, Progress + 0.004f);
            await Task.Delay(100, CancellationToken.None);
        }
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }

    private static void CopyDirectory(string from, string to, Func<string, bool> include)
    {
        if (!Directory.Exists(from)) return;
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (!include(file)) continue;
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void Report(string step, float progress)
    {
        Step = step;
        Progress = progress;
        AddLog($"== {step}");
    }

    private void AddLog(string line)
    {
        Log.Enqueue(line);
        while (Log.Count > 400) Log.TryDequeue(out _);
    }

    private static string SafeFilePart(string s) => string.Concat(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
