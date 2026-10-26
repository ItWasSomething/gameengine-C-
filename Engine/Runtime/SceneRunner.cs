using Engine.Core;
using Engine.Rendering;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Framebuffer = Engine.Rendering.Framebuffer;

namespace Engine.Runtime;

// Plays a .scene file in its own window with no editor: what a shipped game
// would be. It's the editor's Play button minus the UI, so a scene that
// works in the editor works here.
public sealed class SceneRunner
{
    private readonly string _scenePath;
    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private Scene _scene = null!;
    private string? _pendingSceneLoad;
    private Renderer3D _renderer = null!;
    private Framebuffer _output = null!;

    public SceneRunner(string scenePath, string? title = null)
    {
        _scenePath = scenePath;
        _window = Window.Create(WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = title ?? $"MyEngine - {Path.GetFileNameWithoutExtension(scenePath)}",
            VSync = true,
        });
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += _ => OnRender();
        _window.Closing += () => { _renderer.Dispose(); _output.Dispose(); };
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.Escape) _window.Close();
                Input.FeedKey(key, true);
            };
            keyboard.KeyUp += (_, key, _) => Input.FeedKey(key, false);
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (_, b) => Input.FeedMouseButton(b, true);
            mouse.MouseUp += (_, b) => Input.FeedMouseButton(b, false);
            mouse.MouseMove += (_, p) => Input.FeedMousePosition(p);
        }

        // The project folder is the one that contains Assets/.
        AssetDatabase.projectPath = FindProjectFolder(_scenePath);
        TagManager.Load(AssetDatabase.projectPath);
        LoadScripts(AssetDatabase.projectPath);
        SceneManager.LoadSceneHandler = path => _pendingSceneLoad = path;

        _renderer = new Renderer3D(_gl);
        _output = new Framebuffer(_gl, 16, 16, hdr: false);
        StartScene(_scenePath);
    }

    private void StartScene(string file)
    {
        _scene = SceneSerializer.LoadScene(file);
        SceneManager.activeScene = _scene;
        _scene.Play();
    }

    private void OnUpdate(double dt)
    {
        Input.BeginFrame();
        _scene.Tick((float)dt);
        Input.EndFrame();

        if (_pendingSceneLoad is { } next)
        {
            _pendingSceneLoad = null;
            StartScene(AssetDatabase.ToAbsolutePath(next));
        }
    }

    private void OnRender()
    {
        var size = _window.FramebufferSize;
        _output.EnsureSize(size.X, size.Y);

        var camera = Camera.FindRenderCamera(_scene);
        if (camera == null)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ClearColor(0.1f, 0.05f, 0.05f, 1);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            return;
        }

        _renderer.Render(_scene, CameraView.From(camera, _output.Aspect), _output);
        _output.BlitToScreen(size.X, size.Y);
    }

    // The editor compiles the project's scripts into
    // Library/ScriptAssemblies/Assembly-CSharp.dll; the player just loads it.
    // Loaded from bytes (not the file) so the editor can keep rewriting it.
    private static void LoadScripts(string projectPath)
    {
        string dll = Path.Combine(projectPath, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
        if (!File.Exists(dll)) return;
        string pdb = Path.ChangeExtension(dll, ".pdb");
        var assembly = File.Exists(pdb)
            ? System.Reflection.Assembly.Load(File.ReadAllBytes(dll), File.ReadAllBytes(pdb))
            : System.Reflection.Assembly.Load(File.ReadAllBytes(dll));
        TypeRegistry.RegisterAssembly(assembly);
    }

    private static string FindProjectFolder(string scenePath)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(scenePath)!); dir != null; dir = dir.Parent)
            if (dir.Name == "Assets" && dir.Parent != null) return dir.Parent.FullName;
        return Path.GetDirectoryName(scenePath)!;
    }
}
