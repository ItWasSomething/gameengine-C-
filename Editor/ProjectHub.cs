using System.Numerics;
using ImGuiNET;

namespace Editor;

// The first thing you see when the editor starts without a project argument
// (like Unity Hub, but inside the editor window): recent projects, New
// Project, and open-by-path. Picking one calls OnOpen(path, isNew).
public sealed class ProjectHub
{
    private readonly Action<string, bool> _onOpen;
    private readonly string? _sampleProject;

    private string _newName = "My Game";
    private string _newLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyEngine Projects");
    private string _openPath = "";
    private string? _error;

    public ProjectHub(Action<string, bool> onOpen, string? sampleProject)
    {
        _onOpen = onOpen;
        _sampleProject = sampleProject != null && Directory.Exists(sampleProject) ? sampleProject : null;
    }

    public void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, EditorTheme.Bg0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0));
        ImGui.Begin("##ProjectHub", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        // A centered column, at most 880px wide.
        float width = MathF.Min(880, viewport.WorkSize.X - 64);
        float left = (viewport.WorkSize.X - width) / 2;
        ImGui.SetCursorPos(new Vector2(left, MathF.Max(32, viewport.WorkSize.Y * 0.1f)));
        ImGui.BeginGroup();

        ImGui.PushFont(EditorTheme.HeadingFont);
        ImGui.TextUnformatted("MyEngine");
        ImGui.PopFont();
        ImGui.TextDisabled("Open a project, or start a new one.");
        ImGui.Dummy(new Vector2(0, 20));

        float column = (width - 32) / 2;

        // ---- Left: recent projects -------------------------------------------------
        ImGui.BeginChild("recent", new Vector2(column, 420));
        EditorTheme.Label("Recent projects");
        ImGui.Spacing();
        var recents = EditorPrefs.Instance.RecentProjects.Where(Directory.Exists).ToList();
        if (_sampleProject != null && !recents.Any(r => string.Equals(r, _sampleProject, StringComparison.OrdinalIgnoreCase)))
            recents.Add(_sampleProject);
        if (recents.Count == 0) ImGui.TextDisabled("Nothing yet — create a project on the right.");
        foreach (var path in recents)
        {
            if (ProjectRow(path, path == _sampleProject)) _onOpen(path, false);
        }
        ImGui.EndChild();

        ImGui.SameLine(0, 32);

        // ---- Right: new + open -------------------------------------------------------
        ImGui.BeginChild("actions", new Vector2(column, 420));
        EditorTheme.Label("New project");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(column);
        ImGui.InputTextWithHint("##name", "Project name", ref _newName, 64);
        ImGui.SetNextItemWidth(column);
        ImGui.InputTextWithHint("##location", "Folder", ref _newLocation, 260);
        string target = Path.Combine(_newLocation.Trim(), SafeFolder(_newName));
        ImGui.TextDisabled(target);
        ImGui.Spacing();
        if (EditorTheme.PrimaryButton("CREATE PROJECT", new Vector2(column, 36))) CreateProject(target);

        ImGui.Dummy(new Vector2(0, 24));
        EditorTheme.Label("Open existing");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(column);
        bool enter = ImGui.InputTextWithHint("##open", @"C:\path\to\project (contains Assets\)", ref _openPath, 260, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.Button("Open", new Vector2(column, 32)) || enter) OpenExisting(_openPath.Trim().Trim('"'));

        if (_error != null)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + column);
            ImGui.TextColored(EditorTheme.Error, _error);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();

        ImGui.EndGroup();
        ImGui.End();
    }

    // One clickable row: name in bold, path in gray underneath.
    private static bool ProjectRow(string path, bool isSample)
    {
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float h = ImGui.GetTextLineHeight() * 2 + 18;
        bool clicked = ImGui.InvisibleButton("##" + path, new Vector2(w, h));
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRect(start, start + new Vector2(w, h), Widgets.U32(hovered ? EditorTheme.Accent : EditorTheme.Border));
        if (hovered) dl.AddRectFilled(start + new Vector2(1), start + new Vector2(w - 1, h - 1), Widgets.U32(EditorTheme.Bg2));
        string name = new DirectoryInfo(path).Name + (isSample ? "   (sample)" : "");
        ImGui.PushFont(EditorTheme.BoldFont);
        dl.AddText(start + new Vector2(12, 8), Widgets.U32(EditorTheme.Text), name);
        ImGui.PopFont();
        dl.AddText(start + new Vector2(12, 10 + ImGui.GetTextLineHeight()), Widgets.U32(EditorTheme.TextDim), Shorten(path, 60));
        ImGui.Dummy(new Vector2(0, 2));
        return clicked;
    }

    private void CreateProject(string target)
    {
        _error = null;
        if (string.IsNullOrWhiteSpace(_newName)) { _error = "Give the project a name."; return; }
        try
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                _error = "That folder already exists and isn't empty. Pick another name.";
                return;
            }
            Directory.CreateDirectory(Path.Combine(target, "Assets", "Scenes"));
            Directory.CreateDirectory(Path.Combine(target, "Assets", "Scripts"));
            Directory.CreateDirectory(Path.Combine(target, "ProjectSettings"));
            new Build.GameSettings { ProductName = _newName.Trim() }.Save(target);
            // Library/ and Builds/ are generated; keep them out of git from day one.
            File.WriteAllText(Path.Combine(target, ".gitignore"), "Library/\nBuilds/\n*.recovery.scene\nAssembly-CSharp.csproj\n");
            _onOpen(target, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _error = $"Couldn't create the project: {e.Message}";
        }
    }

    private void OpenExisting(string path)
    {
        _error = null;
        if (path.Length == 0) { _error = "Paste the project folder's path."; return; }
        if (!Directory.Exists(Path.Combine(path, "Assets"))) { _error = "That folder isn't a MyEngine project (it has no Assets folder)."; return; }
        _onOpen(Path.GetFullPath(path), false);
    }

    private static string SafeFolder(string name)
    {
        string s = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        return s.Length > 0 ? s : "New Project";
    }

    private static string Shorten(string path, int max) => path.Length <= max ? path : "..." + path[^(max - 3)..];
}
