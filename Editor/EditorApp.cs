using System.Numerics;
using System.Runtime.InteropServices;
using Editor.Panels;
using Editor.Scripting;
using Editor.Viewport;
using Engine;
using Engine.Core;
using Engine.Rendering;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace Editor;

// ProjectDirectory null = show the Project Hub first.
public sealed record EditorOptions(string? ProjectDirectory, string? ScreenshotPath = null, int ScreenshotFrame = 90,
    bool AutoPlay = false, string? SelectOnStart = null, string? OpenScriptOnStart = null, string? SampleProject = null,
    string? OpenWindowOnStart = null);

// The editor application: one OS window with an ImGui dockspace filling it,
// laid out like Unity's default:
//
//   ┌ menu bar ──────────────── ▶ ❚❚ ■ ─────────────────────────┐
//   │ Hierarchy  │        Scene | Game (tabs)        │ Inspector │
//   │            │                                   │           │
//   │            ├───────────────────────────────────┤           │
//   │            │ Project | Console (tabs)          │           │
//   └────────────┴───────────────────────────────────┴───────────┘
//
// Every frame: feed input → update the game (if playing) → build the UI
// (each panel draws itself; the views render the scene into textures they
// show as images) → ImGui renders the UI to the window.
public sealed class EditorApp
{
    private readonly EditorOptions _options;
    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imgui = null!;
    private ImFontPtr _monoFont;
    private IntPtr _iniPath;
    private IntPtr _glyphRanges;
    private Vector2? _lastMouse;

    private EditorContext _ctx = null!;
    private Renderer3D _renderer = null!;
    private HierarchyPanel _hierarchy = null!;
    private InspectorPanel _inspector = null!;
    private ProjectPanel _project = null!;
    private ConsolePanel _console = null!;
    private SceneViewPanel _sceneView = null!;
    private GameViewPanel _gameView = null!;
    private TagsAndLayersWindow _tagsAndLayers = null!;
    private ScriptSystem _scripts = null!;
    private ScriptEditorPanel _scriptEditor = null!;
    private Build.BuildWindow _build = null!;
    private Publishing.PublishWindow _publish = null!;
    private ProjectHub? _hub;   // non-null until a project is open

    private bool _layoutBuilt;
    private bool _showShortcuts;
    private string? _pendingRelaunch;   // project to switch to after the "save changes?" prompt ("" = hub)
    private bool _askSaveBeforeRelaunch;
    private bool _focusGameView;
    private bool _showSaveAs;
    private string _saveAsName = "";
    private bool _showAbout;
    private string? _pendingSceneLoad;
    private int _frame;
    private float _fps = 60;

    public EditorApp(EditorOptions options)
    {
        _options = options;
        _window = Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(1600, 920),
            Title = "MyEngine Editor",
            VSync = true,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        });
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();

        // The game's Input class gets raw events; whether the game may *see*
        // them is decided each frame in OnUpdate.
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => Engine.Input.FeedKey(key, true);
            keyboard.KeyUp += (_, key, _) => Engine.Input.FeedKey(key, false);
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (_, b) => Engine.Input.FeedMouseButton(b, true);
            mouse.MouseUp += (_, b) => Engine.Input.FeedMouseButton(b, false);
            mouse.MouseMove += (_, p) =>
            {
                if (_lastMouse is { } last) Engine.Input.FeedMouseDelta(p - last);
                _lastMouse = p;
            };
        }

        _imgui = new ImGuiController(_gl, _window, _input, ConfigureImGui);
        EditorTheme.Apply();

        if (_options.ProjectDirectory is { } project) OpenProject(project, isNew: false);
        else _hub = new ProjectHub(OpenProject, _options.SampleProject);
    }

    // Everything that needs a project. Runs once: from OnLoad, or when you
    // pick a project in the hub. (Switching projects later restarts the
    // editor process — lots of engine state is static, like Unity's.)
    private void OpenProject(string projectDirectory, bool isNew)
    {
        _hub = null;
        EditorPrefs.Instance.AddRecentProject(projectDirectory);

        _ctx = new EditorContext(projectDirectory);
        _console = new ConsolePanel();
        _ctx.PlayStarted += () =>
        {
            if (_console.ClearOnPlay) _console.Clear();
            _focusGameView = true;
        };
        SceneManager.LoadSceneHandler = path => _pendingSceneLoad = path;
        // Compile scripts before opening the scene, so script components load
        // as themselves rather than as "Missing Script" placeholders.
        _scripts = new ScriptSystem(_ctx);
        _ctx.Scripts = _scripts;
        _ctx.PlayBlocker = () => _scripts.HasErrors
            ? "All script compile errors have to be fixed before you can enter Play mode (see the Console)." : null;
        _scripts.CompileNow();
        OpenStartupScene(isNew);

        _renderer = new Renderer3D(_gl);
        _hierarchy = new HierarchyPanel(_ctx);
        _inspector = new InspectorPanel(_ctx);
        _project = new ProjectPanel(_ctx);
        _sceneView = new SceneViewPanel(_gl, _ctx, _renderer);
        _gameView = new GameViewPanel(_gl, _ctx, _renderer);
        _tagsAndLayers = new TagsAndLayersWindow(_ctx);
        _scriptEditor = new ScriptEditorPanel(_ctx, _scripts);
        _ctx.OpenScript = (file, line) => _scriptEditor.Open(file, line);
        _console.OpenLocation = (file, line) => { if (ResolveScript(file) is { } path) _scriptEditor.Open(path, line); };

        _build = new Build.BuildWindow(_ctx);
        _publish = new Publishing.PublishWindow(_ctx, _build, _gameView.SaveScreenshot);
        _build.OpenPublish = () => { _build.Open = false; _publish.Show(); };
        _scripts.CompileFailed += QuackAtCompileErrors;
        _sceneView.DrawScreenOverlays += DrawDuckBubbles;

        Log.Info($"MyEngine editor ready. Project: {projectDirectory}");
        Log.Info("Scene view: right-drag + WASD to fly, Alt+drag to orbit, middle-drag to pan, F to frame, W/E/R for move/rotate/scale. Help → Keyboard Shortcuts lists the rest.");
        if (_options.SelectOnStart is { } name) _ctx.Select(GameObject.Find(name));
        if (_options.OpenScriptOnStart is { } script) _scriptEditor.Open(script);
        if (_options.OpenWindowOnStart == "build") _build.Open = true;
        if (_options.OpenWindowOnStart == "publish") _publish.Show();
        if (_options.OpenWindowOnStart == "shortcuts") _showShortcuts = true;
    }

    // File → Open Project / recent projects: start a fresh editor process on
    // that project (or on the hub, for "") and close this one.
    private void Relaunch(string project)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        // Under `dotnet Editor.dll` the process is dotnet.exe; pass the dll along.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(EditorApp).Assembly.Location);
        if (project.Length > 0) psi.ArgumentList.Add(project);
        System.Diagnostics.Process.Start(psi);
        _window.Close();
    }

    private void RequestRelaunch(string project)
    {
        _pendingRelaunch = project;
        if (_ctx.IsDirty && !_ctx.IsPlaying && _ctx.ScenePath != null) _askSaveBeforeRelaunch = true;
        else Relaunch(project);
    }

    private void DrawSaveBeforeRelaunch()
    {
        if (_askSaveBeforeRelaunch) { ImGui.OpenPopup("Save changes?"); _askSaveBeforeRelaunch = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal("Save changes?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted($"Save changes to {_ctx.SceneDisplayName.TrimEnd('*')} before switching projects?");
        ImGui.Spacing();
        if (EditorTheme.PrimaryButton("SAVE", new Vector2(110, 0))) { _ctx.SaveScene(); ImGui.CloseCurrentPopup(); Relaunch(_pendingRelaunch!); }
        ImGui.SameLine();
        if (ImGui.Button("Don't save", new Vector2(110, 0)))
        {
            _ctx.MarkClean();
            ImGui.CloseCurrentPopup();
            Relaunch(_pendingRelaunch!);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110, 0))) { _pendingRelaunch = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // Runs inside ImGuiController's constructor, before the font atlas is
    // built — the only point where fonts can be added.
    private unsafe void ConfigureImGui()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.ConfigWindowsMoveFromTitleBarOnly = true;

        // Keep the layout file next to the editor executable rather than in
        // whatever directory it was launched from.
        _iniPath = Marshal.StringToHGlobalAnsi(Path.Combine(AppContext.BaseDirectory, "editor_layout_v4.ini"));
        io.NativePtr->IniFilename = (byte*)_iniPath;

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string ui = Path.Combine(fonts, "segoeui.ttf");
        string mono = Path.Combine(fonts, "consola.ttf");
        // Which characters to bake into the font atlas: Latin-1 plus common
        // punctuation (— “ ” …), arrows and box drawing, so text like an em
        // dash in a log message or a script comment isn't shown as "?".
        // ImGui keeps a pointer to this list, so it lives in unmanaged memory
        // for the whole session.
        ushort[] list = { 0x0020, 0x00FF, 0x2000, 0x206F, 0x2190, 0x21FF, 0x2500, 0x257F, 0x25A0, 0x25FF, 0 };
        _glyphRanges = Marshal.AllocHGlobal(list.Length * sizeof(ushort));
        Marshal.Copy(list.Select(v => (short)v).ToArray(), 0, _glyphRanges, list.Length);
        var ranges = _glyphRanges;
        if (File.Exists(ui)) io.Fonts.AddFontFromFileTTF(ui, 17f, null, ranges);
        else io.Fonts.AddFontDefault();
        _monoFont = File.Exists(mono) ? io.Fonts.AddFontFromFileTTF(mono, 15f, null, ranges) : io.Fonts.Fonts[0];
        string bold = Path.Combine(fonts, "segoeuib.ttf");
        EditorTheme.BoldFont = File.Exists(bold) ? io.Fonts.AddFontFromFileTTF(bold, 17f, null, ranges) : io.Fonts.Fonts[0];
        EditorTheme.HeadingFont = File.Exists(bold) ? io.Fonts.AddFontFromFileTTF(bold, 32f, null, ranges) : io.Fonts.Fonts[0];
    }

    private void OpenStartupScene(bool isNew)
    {
        string main = Path.Combine(AssetDatabase.assetsFolder, "Scenes", "Main" + SceneSerializer.SceneExtension);
        if (File.Exists(main))
        {
            _ctx.OpenScene(main);
            return;
        }
        // First run: build a scene and save it so the project has something
        // in it — the full demo for the sample project, a bare starter for
        // a project made with New Project.
        _ctx.NewScene();
        foreach (var go in _ctx.Scene.GetRootGameObjects().ToList()) EngineObject.DestroyImmediate(go);
        if (isNew) DemoScene.PopulateStarter(); else DemoScene.Populate();
        _ctx.SaveScene(main);
    }

    private void OnUpdate(double delta)
    {
        if (_hub != null) return;   // no project yet
        // The game only sees keyboard/mouse while the Game view has focus
        // and you aren't typing into a text field.
        Engine.Input.Enabled = _ctx.IsPlaying && _gameView.IsFocused && !ImGui.GetIO().WantTextInput;
        // Pick up finished script compiles first, so this frame runs the new code.
        _scripts.Update();
        Engine.Input.BeginFrame();
        _ctx.Scene.Tick((float)delta);
        Engine.Input.EndFrame();

        if (_pendingSceneLoad is { } next)
        {
            _pendingSceneLoad = null;
            if (_ctx.IsPlaying) _ctx.LoadSceneDuringPlay(next);
        }
    }

    private void OnRender(double delta)
    {
        _imgui.Update((float)delta);

        if (_hub != null)
        {
            _hub.Draw();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ClearColor(0, 0, 0, 1);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _imgui.Render();
            _window.Title = "MyEngine";
            if (_options.ScreenshotPath != null && ++_frame == _options.ScreenshotFrame)
            {
                SaveWindowScreenshot(_options.ScreenshotPath);
                _window.Close();
            }
            return;
        }

        HandleShortcuts();
        DuckAdviceOnSelect();
        DrawMainWindow();
        _hierarchy.Draw();
        _project.Draw();
        _console.Draw(_monoFont);
        _inspector.Draw();
        // Keep asking until the Game window exists and is the visible tab
        // (on the very first frame it hasn't been created yet).
        if (_focusGameView) { _gameView.RequestFocus = true; _focusGameView = false; }
        _gameView.Draw();
        _sceneView.Draw((float)delta);
        _tagsAndLayers.Draw();
        _scriptEditor.Draw(_monoFont);
        _build.Draw();
        _publish.Draw();
        DrawSaveAsDialog();
        DrawSaveBeforeRelaunch();
        DrawAboutWindow();
        DrawShortcutsWindow();
        Debug.ExpireLines();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fb = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _gl.ClearColor(0, 0, 0, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _imgui.Render();

        _window.Title = $"{_ctx.SceneDisplayName} - MyEngine Editor" + (_ctx.IsPlaying ? "  [PLAYING]" : "");

        _frame++;
        // --play presses Play after the UI has settled, like a real click.
        if (_options.AutoPlay && _frame == 30) _ctx.Play();
        if (_options.ScreenshotPath != null && _frame == _options.ScreenshotFrame)
        {
            SaveWindowScreenshot(_options.ScreenshotPath);
            _window.Close();
        }
    }

    private unsafe void SaveWindowScreenshot(string path)
    {
        var size = _window.FramebufferSize;
        var pixels = new byte[size.X * size.Y * 4];
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        PngWriter.Save(path, size.X, size.Y, pixels);
        Log.Info($"Screenshot saved to {path}");
    }

    // ---- Main window: menu bar, play controls, dockspace ----------------------------

    private void DrawMainWindow()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        var flags = ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, EditorTheme.Bg0);   // the "gap" color between cards
        ImGui.Begin("##EditorRoot", flags);
        ImGui.PopStyleColor();
        var rootPos = ImGui.GetWindowPos();
        EditorTheme.DrawTitleStrip(rootPos, rootPos + new Vector2(ImGui.GetWindowWidth(), ImGui.GetFrameHeight() + 4), _ctx.IsPlaying);
        ImGui.PopStyleVar(3);

        DrawMenuBar();

        uint dockId = ImGui.GetID("EditorDockSpace");
        if (!_layoutBuilt)
        {
            if (DockBuilder.GetNode(dockId) == IntPtr.Zero) BuildDefaultLayout(dockId, viewport.WorkSize);
            _layoutBuilt = true;
        }
        // Leave room at the bottom for the status bar.
        ImGui.DockSpace(dockId, new Vector2(0, -EditorTheme.StatusBarHeight), ImGuiDockNodeFlags.None);
        DrawStatusBar();
        ImGui.End();
    }

    // The strip along the bottom: play state, scene, selection and script
    // status on the left; frame rate and object count on the right.
    private void DrawStatusBar()
    {
        var io = ImGui.GetIO();
        _fps += (io.Framerate - _fps) * 0.05f;
        var start = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(start + new Vector2(10, 1));

        var (label, color) = _ctx.State switch
        {
            PlayState.Playing => ("PLAYING", EditorTheme.Accent),
            PlayState.Paused => ("PAUSED", EditorTheme.Warning),
            _ => ("EDITING", EditorTheme.Success),
        };
        EditorTheme.StatusDot(color);
        ImGui.SameLine(0, 6);
        ImGui.AlignTextToFramePadding();
        ImGui.PushFont(EditorTheme.BoldFont);
        ImGui.TextColored(color, label);
        ImGui.PopFont();

        void Divider()
        {
            ImGui.SameLine(0, 12);
            ImGui.TextColored(EditorTheme.Border, "|");
            ImGui.SameLine(0, 12);
        }

        Divider();
        ImGui.TextDisabled("Scene");
        ImGui.SameLine();
        ImGui.TextUnformatted(_ctx.SceneDisplayName);
        if (_ctx.Selected is { } selected && selected)
        {
            Divider();
            ImGui.TextDisabled("Selected");
            ImGui.SameLine();
            ImGui.TextColored(Widgets.GameObjectColor(selected), selected.name);
        }

        Divider();
        int errors = _scripts.Diagnostics.Count(d => d.IsError);
        if (_scripts.IsCompiling)
        {
            EditorTheme.StatusDot(EditorTheme.Warning);
            ImGui.SameLine(0, 6);
            ImGui.TextColored(EditorTheme.Warning, "Compiling scripts...");
        }
        else if (errors > 0)
        {
            EditorTheme.StatusDot(EditorTheme.Error);
            ImGui.SameLine(0, 6);
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            // Click to jump straight to the first error.
            if (ImGui.Selectable($"{errors} script error{(errors == 1 ? "" : "s")}", false, ImGuiSelectableFlags.None,
                    ImGui.CalcTextSize($"{errors} script errors")))
            {
                var first = _scripts.Diagnostics.First(d => d.IsError);
                _scriptEditor.Open(first.File, first.Line);
            }
            ImGui.PopStyleColor();
        }
        else
        {
            EditorTheme.StatusDot(EditorTheme.Success);
            ImGui.SameLine(0, 6);
            ImGui.TextDisabled("Scripts OK");
        }

        string right = $"{_fps:0} fps   {_ctx.Scene.AllGameObjects().Count()} objects   MyEngine 0.1";
        ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(right).X - 18);
        ImGui.TextDisabled(right);
    }

    private static void BuildDefaultLayout(uint dockId, Vector2 size)
    {
        DockBuilder.RemoveNode(dockId);
        DockBuilder.AddNode(dockId, DockBuilder.DockSpaceFlag);
        DockBuilder.SetNodeSize(dockId, size);

        uint center = dockId;
        uint left = DockBuilder.Split(ref center, ImGuiDir.Left, 0.18f);
        uint right = DockBuilder.Split(ref center, ImGuiDir.Right, 0.25f);
        uint bottom = DockBuilder.Split(ref center, ImGuiDir.Down, 0.28f);

        DockBuilder.DockWindow(HierarchyPanel.Title, left);
        DockBuilder.DockWindow(InspectorPanel.Title, right);
        DockBuilder.DockWindow(ProjectPanel.Title, bottom);
        DockBuilder.DockWindow(ConsolePanel.Title, bottom);
        DockBuilder.DockWindow(GameViewPanel.Title, center);
        DockBuilder.DockWindow(ScriptEditorPanel.Title, center);
        DockBuilder.DockWindow(SceneViewPanel.Title, center);   // docked last = the tab shown first
        DockBuilder.Finish(dockId);
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;

        EditorTheme.DrawLogo();
        ImGui.Spacing();

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene", "Ctrl+N")) _ctx.NewScene();
            if (ImGui.BeginMenu("Open Scene"))
            {
                var scenes = Directory.GetFiles(AssetDatabase.assetsFolder, "*" + SceneSerializer.SceneExtension, SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".recovery" + SceneSerializer.SceneExtension)).ToArray();
                if (scenes.Length == 0) ImGui.TextDisabled("No scenes in project");
                foreach (var scene in scenes)
                    if (ImGui.MenuItem(AssetDatabase.ToAssetPath(scene))) _ctx.OpenScene(scene);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Save", "Ctrl+S", false, !_ctx.IsPlaying)) _ctx.SaveScene();
            if (ImGui.MenuItem("Save As...", "Ctrl+Shift+S", false, !_ctx.IsPlaying)) OpenSaveAs();
            ImGui.Separator();
            if (ImGui.MenuItem("Open Project...")) RequestRelaunch("");
            if (ImGui.BeginMenu("Recent Projects"))
            {
                var recents = EditorPrefs.Instance.RecentProjects
                    .Where(p => Directory.Exists(p) && !string.Equals(p, _ctx.ProjectDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
                if (recents.Count == 0) ImGui.TextDisabled("No other projects yet");
                foreach (var p in recents)
                {
                    if (ImGui.MenuItem(new DirectoryInfo(p).Name)) RequestRelaunch(p);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);
                }
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Build Settings...", "Ctrl+Shift+B")) _build.Open = true;
            if (ImGui.MenuItem("Build Game", "Ctrl+B", false, !_build.Builder.IsRunning && !_ctx.IsPlaying)) { _build.Open = true; _build.StartBuild(); }
            if (ImGui.MenuItem("Publish to MyEngine Games...")) _publish.Show();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit", "Alt+F4")) _window.Close();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _ctx.CanUndo)) _ctx.Undo();
            if (ImGui.MenuItem("Redo", "Ctrl+Y", false, _ctx.CanRedo)) _ctx.Redo();
            ImGui.Separator();
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, _ctx.Selected != null)) _ctx.DuplicateSelected();
            if (ImGui.MenuItem("Delete", "Del", false, _ctx.Selected != null)) _ctx.DeleteSelected();
            if (ImGui.MenuItem("Frame Selected", "F", false, _ctx.Selected != null)) _sceneView.FrameSelected();
            ImGui.Separator();
            if (ImGui.MenuItem("Play", "Ctrl+P")) TogglePlay();
            if (ImGui.MenuItem("Pause", "Ctrl+Shift+P", false, _ctx.IsPlaying)) TogglePause();
            ImGui.Separator();
            if (ImGui.BeginMenu("Project Settings"))
            {
                if (ImGui.MenuItem("Tags and Layers")) _ctx.ShowTagsAndLayers = true;
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Assets"))
        {
            if (ImGui.MenuItem("Create C# Script"))
            {
                if (_scripts.CreateScript("NewBehaviour", null) is { } file) _scriptEditor.Open(file);
            }
            if (ImGui.MenuItem("Refresh")) _project.Refresh();
            if (ImGui.MenuItem("Show in Explorer"))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{AssetDatabase.assetsFolder}\"");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("GameObject"))
        {
            CreateMenu.Items(_ctx, null);
            ImGui.Separator();
            if (ImGui.MenuItem("Create Empty Child", null, false, _ctx.Selected != null))
                _ctx.CreateGameObject("Empty", _ctx.Selected!.transform);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Component"))
        {
            if (_ctx.Selected is not { } selected) ImGui.TextDisabled("Select a GameObject first");
            else
            {
                foreach (var group in TypeRegistry.AddableComponents.GroupBy(t => TypeRegistry.MenuPath(t).Split('/')[0]))
                {
                    if (!ImGui.BeginMenu(group.Key)) continue;
                    foreach (var t in group)
                        if (ImGui.MenuItem(Widgets.NiceName(TypeRegistry.MenuPath(t).Split('/').Last())))
                            _ctx.AddComponent(selected, t);
                    ImGui.EndMenu();
                }
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window"))
        {
            if (ImGui.MenuItem("Script Editor")) _scriptEditor.Show();
            if (ImGui.MenuItem("Reset Layout"))
                BuildDefaultLayout(ImGui.GetID("EditorDockSpace"), ImGui.GetMainViewport().WorkSize);
            if (ImGui.MenuItem("Save Screenshot"))
                SaveWindowScreenshot(Path.Combine(_ctx.ProjectDirectory, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Keyboard Shortcuts", "F1")) _showShortcuts = true;
            if (ImGui.MenuItem("Open MyEngine Games website"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EditorPrefs.Instance.SiteUrl) { UseShellExecute = true });
            ImGui.Separator();
            if (ImGui.MenuItem("About MyEngine")) _showAbout = true;
            ImGui.EndMenu();
        }

        DrawPlayControls();

        // Script compile status, then the scene name, on the right.
        string sceneLabel = _ctx.SceneDisplayName;
        int scriptErrors = _scripts.Diagnostics.Count(d => d.IsError);
        string scriptStatus = _scripts.IsCompiling ? "Compiling scripts..." : scriptErrors > 0 ? $"{scriptErrors} script error{(scriptErrors == 1 ? "" : "s")}" : "";
        if (scriptStatus.Length > 0)
        {
            ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(sceneLabel).X - ImGui.CalcTextSize(scriptStatus).X - 40);
            ImGui.TextColored(_scripts.IsCompiling ? EditorTheme.Warning : EditorTheme.Error, scriptStatus);
        }
        ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(sceneLabel).X - 16);
        ImGui.TextDisabled(sceneLabel);

        ImGui.EndMenuBar();
    }

    private void TogglePlay()
    {
        if (_ctx.IsPlaying) _ctx.Stop(); else _ctx.Play();
    }

    private void TogglePause()
    {
        if (_ctx.State == PlayState.Paused) _ctx.Play(); else _ctx.Pause();
    }

    // Play / Pause / Step centered in the menu bar, like Unity's toolbar.
    // Play toggles: pressing it while playing stops.
    private void DrawPlayControls()
    {
        const float button = 26, gap = 2;
        float total = button * 3 + gap * 2;
        ImGui.SameLine((ImGui.GetWindowWidth() - total) / 2);

        bool playing = _ctx.IsPlaying;
        bool paused = _ctx.State == PlayState.Paused;

        if (Widgets.IconButton("##play", playing ? Icon.Stop : Icon.Play, playing,
                playing ? "Stop (Ctrl+P)" : "Play (Ctrl+P)", EditorTheme.Text, button))
            TogglePlay();
        ImGui.SameLine(0, gap);
        if (Widgets.IconButton("##pause", Icon.Pause, paused, "Pause (Ctrl+Shift+P)", null, button))
        {
            if (!playing) { _ctx.Play(); _ctx.Pause(); }
            else TogglePause();
        }
        ImGui.SameLine(0, gap);
        if (Widgets.IconButton("##step", Icon.Step, false, "Step one fixed frame (Ctrl+Alt+P, while paused)",
                paused ? EditorTheme.Text : EditorTheme.TextDim, button))
            _ctx.StepFrame();
    }

    private void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput || Code.CodeEditor.AnyFocused) return;   // typing: leave keys alone
        bool ctrl = io.KeyCtrl, shift = io.KeyShift;

        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.P, false))
        {
            if (io.KeyAlt) _ctx.StepFrame();
            else if (shift) TogglePause();
            else TogglePlay();
        }

        // While the game has the keyboard, leave plain keys to it.
        if (_ctx.IsPlaying && _gameView.IsFocused && !ctrl) return;

        if (ImGui.IsKeyPressed(ImGuiKey.F1, false)) _showShortcuts = !_showShortcuts;
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.B, false))
        {
            _build.Open = true;
            if (!shift) _build.StartBuild();
        }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) { if (shift) OpenSaveAs(); else _ctx.SaveScene(); }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.N, false)) _ctx.NewScene();
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z)) { if (shift) _ctx.Redo(); else _ctx.Undo(); }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y)) _ctx.Redo();
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) _ctx.DuplicateSelected();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) _ctx.DeleteSelected();
        if (ImGui.IsKeyPressed(ImGuiKey.F2, false) && _ctx.Selected != null) _hierarchy.BeginRename(_ctx.Selected);
    }

    private void OpenSaveAs()
    {
        _saveAsName = _ctx.ScenePath != null ? Path.GetFileNameWithoutExtension(_ctx.ScenePath) : "New Scene";
        _showSaveAs = true;
    }

    private void DrawSaveAsDialog()
    {
        if (_showSaveAs) { ImGui.OpenPopup("Save Scene As"); _showSaveAs = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        bool open = true;
        if (!ImGui.BeginPopupModal("Save Scene As", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Saved into Assets/Scenes/");
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(320);
        bool enter = ImGui.InputText(SceneSerializer.SceneExtension, ref _saveAsName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        string fileName = string.Concat(_saveAsName.Trim().Split(Path.GetInvalidFileNameChars()));
        if ((ImGui.Button("Save", new Vector2(120, 0)) || enter) && fileName.Length > 0)
        {
            _ctx.SaveScene(Path.Combine(AssetDatabase.assetsFolder, "Scenes", fileName + SceneSerializer.SceneExtension));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawAboutWindow()
    {
        if (!_showAbout) return;
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (ImGui.Begin("About MyEngine", ref _showAbout, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking))
        {
            ImGui.TextColored(EditorTheme.Accent, "MyEngine");
            ImGui.Text("A from-scratch C# / OpenGL engine, built on a devlog.");
            ImGui.Separator();
            ImGui.TextDisabled($".NET {Environment.Version}   OpenGL {_gl.GetStringS(StringName.Version)}");
            ImGui.TextDisabled($"Renderer: {_gl.GetStringS(StringName.Renderer)}");
        }
        ImGui.End();
    }

    // ---- Rubber ducks (see Engine/Core/RubberDuck.cs) ----------------------------------
    //
    // The duck itself only holds settings + its quack lines; reacting to the
    // editor (compile errors, selection) and the speech bubbles live here,
    // because the engine knows nothing about the editor.

    private readonly Dictionary<RubberDuck, (string Text, double Until)> _duckBubbles = new();
    private GameObject? _lastSelected;

    private void QuackAtCompileErrors(int errorCount)
    {
        foreach (var duck in _ctx.Scene.FindComponents<RubberDuck>())
        {
            if (!duck.enabled || !duck.quackOnCompileErrors) continue;
            string line = duck.Quack(errorCount);
            Log.Warn($"[{duck.gameObject.name}] {line}");
            _duckBubbles[duck] = ("QUACK!", ImGui.GetTime() + 2.5);
        }
    }

    // Selecting a duck (or any part of one) gets you one piece of advice.
    private void DuckAdviceOnSelect()
    {
        var selected = _ctx.Selected;
        if (selected == _lastSelected) return;
        _lastSelected = selected;
        if (selected is null || !selected || selected.GetComponentInParent<RubberDuck>() is not { } duck) return;
        string advice = RubberDuck.RandomWisdom();
        Log.Info($"[{duck.gameObject.name}] {advice}");
        _duckBubbles[duck] = (advice, ImGui.GetTime() + 4);
    }

    // A white speech bubble above each talking duck, in the Scene view.
    private void DrawDuckBubbles(ImDrawListPtr dl, Vector2 imageMin, Vector2 imageSize, CameraView view)
    {
        double now = ImGui.GetTime();
        foreach (var (duck, (text, until)) in _duckBubbles.ToList())
        {
            if (!duck || now > until) { _duckBubbles.Remove(duck); continue; }
            var head = duck.transform.position + new Vector3(0, 1.45f * duck.transform.lossyScale.Y, 0);
            if (!ViewportMath.WorldToScreen(head, imageSize, view, out var pixel)) continue;

            // Pop in over the first 0.15 s, fade out over the last 0.4 s.
            float age = (float)(now - (until - (text == "QUACK!" ? 2.5 : 4)));
            float pop = MathF.Min(1, age / 0.15f);
            float alpha = (float)Math.Clamp((until - now) / 0.4, 0, 1);
            var textSize = ImGui.CalcTextSize(text);
            var tip = imageMin + pixel - new Vector2(0, 6 * pop);
            var min = tip - new Vector2(textSize.X / 2 + 10, textSize.Y + 18);
            var max = tip + new Vector2(textSize.X / 2 + 10, -8);
            dl.PushClipRect(imageMin, imageMin + imageSize, true);
            dl.AddRectFilled(min + new Vector2(3), max + new Vector2(3), Widgets.U32(new Vector4(0, 0, 0, 0.5f * alpha)));
            dl.AddRectFilled(min, max, Widgets.U32(new Vector4(1, 1, 1, alpha)));
            dl.AddTriangleFilled(new Vector2(tip.X - 6, max.Y), new Vector2(tip.X + 6, max.Y), tip, Widgets.U32(new Vector4(1, 1, 1, alpha)));
            dl.AddText(min + new Vector2(10, 5), Widgets.U32(new Vector4(0, 0, 0, alpha)), text);
            dl.PopClipRect();
        }
    }

    // Help → Keyboard Shortcuts (F1): every shortcut in one place, so they're
    // discoverable without reading the source.
    private static readonly (string Group, (string Keys, string What)[] Items)[] Shortcuts =
    [
        ("General", [("Ctrl+S", "Save scene"), ("Ctrl+Shift+S", "Save scene as"), ("Ctrl+N", "New scene"),
                     ("Ctrl+Z / Ctrl+Y", "Undo / redo"), ("Ctrl+B", "Build game"), ("Ctrl+Shift+B", "Build settings"), ("F1", "This list")]),
        ("Play mode", [("Ctrl+P", "Play / stop"), ("Ctrl+Shift+P", "Pause"), ("Ctrl+Alt+P", "Step one frame (paused)")]),
        ("Selection", [("Ctrl+D", "Duplicate"), ("Delete", "Delete"), ("F2", "Rename"), ("F", "Frame in Scene view")]),
        ("Scene view", [("Right-drag + WASD/QE", "Fly"), ("Alt + left-drag", "Orbit"), ("Middle-drag", "Pan"),
                        ("Scroll", "Zoom"), ("W / E / R", "Move / rotate / scale gizmo")]),
        ("Script editor", [("Ctrl+S", "Save + recompile"), ("Ctrl+F", "Find"), ("Ctrl+Space", "Autocomplete")]),
    ];

    private void DrawShortcutsWindow()
    {
        if (!_showShortcuts) return;
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (ImGui.Begin("Keyboard Shortcuts", ref _showShortcuts, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse))
        {
            foreach (var (group, items) in Shortcuts)
            {
                EditorTheme.Label(group);
                if (ImGui.BeginTable(group, 2, ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("keys", ImGuiTableColumnFlags.WidthFixed, 190);
                    foreach (var (keys, what) in items)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.PushFont(_monoFont);
                        ImGui.TextUnformatted(keys);
                        ImGui.PopFont();
                        ImGui.TableNextColumn();
                        ImGui.TextDisabled(what);
                    }
                    ImGui.EndTable();
                }
                ImGui.Spacing();
            }
        }
        ImGui.End();
    }

    // Console locations are project-relative ("Assets/Scripts/Foo.cs") for
    // compile errors, or just "Foo.cs" for runtime exceptions.
    private string? ResolveScript(string file)
    {
        if (Path.IsPathRooted(file) && File.Exists(file)) return file;
        string relative = Path.Combine(_ctx.ProjectDirectory, file);
        if (File.Exists(relative)) return relative;
        return Directory.EnumerateFiles(AssetDatabase.assetsFolder, Path.GetFileName(file), SearchOption.AllDirectories).FirstOrDefault();
    }

    private void OnClosing()
    {
        // _ctx is null if the window closes while still on the Project Hub.
        if (_ctx != null)
        {
            if (_ctx.IsDirty && !_ctx.IsPlaying && _ctx.ScenePath != null)
            {
                // Closing the window can't be cancelled, so there's no "save
                // changes?" prompt here; don't silently lose work — write a
                // recovery copy next to the scene.
                string recovery = Path.ChangeExtension(_ctx.ScenePath, ".recovery" + SceneSerializer.SceneExtension);
                SceneSerializer.SaveScene(_ctx.Scene, recovery);
                Log.Warn($"Unsaved changes written to {AssetDatabase.ToAssetPath(recovery)}");
            }
            _build.Builder.Cancel();
            _sceneView.Dispose();
            _scripts.Dispose();
            _gameView.Dispose();
            _renderer.Dispose();
        }
        _imgui.Dispose();
        _input.Dispose();
        _gl.Dispose();
        if (_iniPath != IntPtr.Zero) Marshal.FreeHGlobal(_iniPath);
        if (_glyphRanges != IntPtr.Zero) Marshal.FreeHGlobal(_glyphRanges);
    }
}
