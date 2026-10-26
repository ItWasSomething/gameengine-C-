using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.Input;

namespace Engine;

public class GameWindow
{
    private IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;

    private Shader _spriteShader = null!;
    private Texture _playerTexture = null!;
    private Texture _platformTexture = null!;
    private Texture _particleTexture = null!;
    private Texture _targetTexture = null!;
    private Texture _slashTexture = null!;
    private Sprite _playerSprite = null!;
    private Sprite _platformSprite = null!;
    private Sprite _particleSprite = null!;
    private Sprite _targetSprite = null!;
    private Sprite _slashSprite = null!;

    // Lighting pipeline: the scene is drawn into _sceneTarget (not the
    // window), _lighting draws its light map, and _compositeShader multiplies
    // the two onto the screen via _fullscreenQuad.
    private Shader _lightShader = null!;
    private Shader _compositeShader = null!;
    private Lighting _lighting = null!;
    private RenderTarget _sceneTarget = null!;
    private FullscreenQuad _fullscreenQuad = null!;
    private PointLight2D _playerLight = null!;
    private readonly List<Vector2D<float>> _lanterns = new();

    private static readonly Vector3 SkyColor = new(0.62f, 0.5f, 0.72f); // dusky violet, before lighting
    private static readonly Vector3 LanternColor = new(1f, 0.62f, 0.3f);

    private Player _player = null!;
    private readonly List<Aabb> _platforms = new();
    private readonly List<Target> _targets = new();
    private readonly ParticleSystem _dustParticles = new();
    // Separate pool from _dustParticles purely because it draws with a
    // different texture (slash.png vs particle.png) — one ParticleSystem
    // draws every live particle in it with a single Sprite, so different
    // looks need different pools rather than a per-particle texture id.
    private readonly ParticleSystem _slashParticles = new(capacity: 8);

    // Targets a swing has already damaged, cleared every time a new swing
    // starts — without this, one swing would deal damage every single frame
    // its hitbox happens to be active, instead of once per swing.
    private readonly HashSet<Target> _hitThisSwing = new();

    // Dust color shared by jump/land bursts — a pale dirt/sand tone.
    private static readonly Vector3 DustColor = new(0.85f, 0.8f, 0.65f);
    private static readonly Vector3 SparkColor = new(1f, 0.95f, 0.5f);

    private Vector2D<int> _windowSize;
    // Can differ from _windowSize on high-DPI displays: _windowSize is our
    // world/projection size, this is the actual pixel count GL renders into.
    private Vector2D<int> _framebufferSize;
    private Matrix4x4 _projection;

    public GameWindow(string title = "My Engine", int width = 1280, int height = 720)
    {
        _windowSize = new Vector2D<int>(width, height);

        var options = WindowOptions.Default with
        {
            Size  = _windowSize,
            Title = title
        };

        _window = Window.Create(options);

        _window.Load     += OnLoad;    // runs once, when the window is ready
        _window.Update    += OnUpdate; // runs every frame: game logic
        _window.Render    += OnRender; // runs every frame: drawing
        _window.Resize    += OnResize;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing   += OnClosing;
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];

        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

        _spriteShader = new Shader(_gl,
            Path.Combine(shaderDir, "sprite.vert"),
            Path.Combine(shaderDir, "sprite.frag"));

        _playerTexture = new Texture(_gl, Path.Combine(assetsDir, "player.png"));
        _platformTexture = new Texture(_gl, Path.Combine(assetsDir, "platform.png"));
        _particleTexture = new Texture(_gl, Path.Combine(assetsDir, "particle.png"));
        _targetTexture = new Texture(_gl, Path.Combine(assetsDir, "target.png"));
        _slashTexture = new Texture(_gl, Path.Combine(assetsDir, "slash.png"));
        _playerSprite = new Sprite(_gl, _spriteShader, _playerTexture);
        _platformSprite = new Sprite(_gl, _spriteShader, _platformTexture);
        _particleSprite = new Sprite(_gl, _spriteShader, _particleTexture);
        _targetSprite = new Sprite(_gl, _spriteShader, _targetTexture);
        _slashSprite = new Sprite(_gl, _spriteShader, _slashTexture);

        // light.frag reuses sprite.vert — a light is placed exactly like a
        // sprite, it just colors its pixels differently.
        _lightShader = new Shader(_gl,
            Path.Combine(shaderDir, "sprite.vert"),
            Path.Combine(shaderDir, "light.frag"));
        _compositeShader = new Shader(_gl,
            Path.Combine(shaderDir, "fullscreen.vert"),
            Path.Combine(shaderDir, "composite.frag"));

        _framebufferSize = _window.FramebufferSize;
        _sceneTarget = new RenderTarget(_gl, _framebufferSize.X, _framebufferSize.Y);
        _lighting = new Lighting(_gl, _lightShader, _particleTexture, _framebufferSize.X, _framebufferSize.Y);
        _fullscreenQuad = new FullscreenQuad(_gl);

        _player = new Player { Position = new Vector2D<float>(100f, 64f) };

        // Player doesn't know particles exist — it just announces "I jumped" /
        // "I landed" and GameWindow decides what that looks like.
        _player.Jumped += () => _dustParticles.Emit(
            FeetPosition(), count: 8, baseDirection: new Vector2D<float>(0f, -1f), spreadDegrees: 100f,
            minSpeed: 30f, maxSpeed: 90f, minLife: 0.15f, maxLife: 0.3f,
            startSize: 8f, endSize: 0f, color: DustColor);

        _player.Landed += () => _dustParticles.Emit(
            FeetPosition(), count: 14, baseDirection: new Vector2D<float>(0f, 1f), spreadDegrees: 160f,
            minSpeed: 40f, maxSpeed: 150f, minLife: 0.25f, maxLife: 0.45f,
            startSize: 10f, endSize: 0f, color: DustColor);

        // A new swing must be allowed to hit every target fresh.
        _player.AttackStarted += () => _hitThisSwing.Clear();

        BuildLevel();
        BuildTargets();
        BuildLights();
        UpdateProjection();

        _keyboard.KeyDown += (_, key, _) =>
        {
            if (IsJumpKey(key)) _player.BufferJump();
            if (IsAttackKey(key)) _player.TryAttack();
        };
        _keyboard.KeyUp += (_, key, _) => { if (IsJumpKey(key)) _player.CutJumpIfRising(); };
    }

    private Vector2D<float> FeetPosition() =>
        new(_player.Position.X + _player.Size.X / 2f, _player.Position.Y);

    private void BuildTargets()
    {
        // One on the ground, one up on a platform — close enough to reach
        // on foot, no combat AI, just something to swing at.
        AddTarget(new Vector2D<float>(420f, 64f));
        AddTarget(new Vector2D<float>(600f, 312f));
    }

    private void AddTarget(Vector2D<float> position)
    {
        var target = new Target { Position = position };

        // Sparks continue in the direction the hit came from (away from the
        // player, the same way the swing was already moving), not back at it.
        target.Hit += () => _dustParticles.Emit(
            target.Center, count: 10, baseDirection: new Vector2D<float>(_player.FacingDirection, 0.3f),
            spreadDegrees: 90f, minSpeed: 60f, maxSpeed: 180f, minLife: 0.15f, maxLife: 0.3f,
            startSize: 7f, endSize: 0f, color: SparkColor);

        // The slash mark itself: doesn't fly anywhere (min/max speed 0), just
        // sits at the hit point, growing slightly and sweeping through a
        // small arc while it fades — a quick "swipe" rather than a projectile.
        // slash.png is baked opening toward +X, so facing left needs a half
        // turn (PI) to open the other way instead of a separate mirrored asset.
        target.Hit += () =>
        {
            float baseAngle = _player.FacingDirection > 0f ? 0f : MathF.PI;
            _slashParticles.Emit(
                target.Center, count: 1, baseDirection: new Vector2D<float>(1f, 0f), spreadDegrees: 0f,
                minSpeed: 0f, maxSpeed: 0f, minLife: 0.12f, maxLife: 0.12f,
                startSize: 56f, endSize: 74f, color: new Vector3(1f, 1f, 1f),
                startRotation: baseAngle - 0.3f, endRotation: baseAngle + 0.3f);
        };

        target.Died += () =>
        {
            _dustParticles.Emit(
                target.Center, count: 22, baseDirection: new Vector2D<float>(0f, 1f), spreadDegrees: 360f,
                minSpeed: 60f, maxSpeed: 220f, minLife: 0.25f, maxLife: 0.5f,
                startSize: 9f, endSize: 0f, color: SparkColor);
        };

        // Every impact briefly lights up its surroundings — at dusk this
        // reads far more than the sparks alone.
        target.Hit += () => _lighting.Flash(target.Center, radius: 170f, SparkColor, intensity: 1.6f, lifetime: 0.18f);
        target.Died += () => _lighting.Flash(target.Center, radius: 320f, SparkColor, intensity: 2.5f, lifetime: 0.4f);

        _targets.Add(target);
    }

    private void BuildLights()
    {
        // The player carries a small warm light so they never vanish into
        // the dark; its Position is updated every frame in OnUpdate.
        _playerLight = new PointLight2D { Radius = 170f, Color = new Vector3(1f, 0.85f, 0.65f), Intensity = 0.7f };
        _lighting.Lights.Add(_playerLight);

        // Floating lanterns hovering above each platform.
        _lanterns.Add(new Vector2D<float>(350f, 240f));
        _lanterns.Add(new Vector2D<float>(650f, 370f));
        _lanterns.Add(new Vector2D<float>(960f, 290f));
        _lanterns.Add(new Vector2D<float>(1200f, 430f));
        foreach (var pos in _lanterns)
        {
            _lighting.Lights.Add(new PointLight2D
            {
                Position = pos, Radius = 260f, Color = LanternColor, Intensity = 1.3f, Flicker = 0.12f,
            });
        }
    }

    private void BuildLevel()
    {
        // Hand-placed rectangles for now — this is the AABB collision half
        // of Episode 5, done ahead of Episode 4's proper tile-grid loader.
        _platforms.Add(new Aabb(new Vector2D<float>(0f, 0f), new Vector2D<float>(1280f, 64f)));       // ground
        _platforms.Add(new Aabb(new Vector2D<float>(250f, 160f), new Vector2D<float>(200f, 32f)));
        _platforms.Add(new Aabb(new Vector2D<float>(550f, 280f), new Vector2D<float>(200f, 32f)));
        _platforms.Add(new Aabb(new Vector2D<float>(850f, 200f), new Vector2D<float>(220f, 32f)));
        _platforms.Add(new Aabb(new Vector2D<float>(1120f, 340f), new Vector2D<float>(160f, 32f)));
    }

    private static bool IsJumpKey(Key key) => key is Key.Space or Key.W or Key.Up;
    private static bool IsAttackKey(Key key) => key is Key.J or Key.X or Key.Enter;

    private void OnUpdate(double deltaTime)
    {
        float dt = (float)deltaTime;

        float moveInput = 0f;
        if (_keyboard.IsKeyPressed(Key.A) || _keyboard.IsKeyPressed(Key.Left)) moveInput -= 1f;
        if (_keyboard.IsKeyPressed(Key.D) || _keyboard.IsKeyPressed(Key.Right)) moveInput += 1f;

        _player.Update(dt, moveInput, _platforms);
        _dustParticles.Update(dt);
        _slashParticles.Update(dt);

        foreach (var target in _targets)
            target.Update(dt);

        if (_player.IsAttackHitboxActive)
        {
            var hitbox = _player.GetAttackHitbox();
            foreach (var target in _targets)
            {
                if (target.IsAlive && !_hitThisSwing.Contains(target) && hitbox.Intersects(target.Bounds))
                {
                    target.TakeDamage();
                    _hitThisSwing.Add(target);
                }
            }
        }

        _targets.RemoveAll(t => !t.IsAlive);

        _playerLight.Position = new Vector2D<float>(
            _player.Position.X + _player.Size.X / 2f,
            _player.Position.Y + _player.Size.Y / 2f);
        _lighting.Update(dt);
    }

    private void OnRender(double deltaTime)
    {
        // Pass 1: how much light reaches each pixel.
        _lighting.RenderLightMap(_projection);

        // Pass 2: the scene exactly as before, just into an offscreen
        // target instead of the window.
        _sceneTarget.BindAsTarget();
        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        DrawScene();

        // Pass 3: scene * light, onto the actual window (framebuffer 0).
        // Blending off — we're replacing every pixel, not layering on top.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_framebufferSize.X, (uint)_framebufferSize.Y);
        _gl.Disable(EnableCap.Blend);
        _compositeShader.Use();
        _sceneTarget.BindTexture(TextureUnit.Texture0);
        _compositeShader.SetUniform("uScene", 0);
        _lighting.LightMap.BindTexture(TextureUnit.Texture1);
        _compositeShader.SetUniform("uLightMap", 1);
        _fullscreenQuad.Draw();
        _gl.Enable(EnableCap.Blend);
    }

    private void DrawScene()
    {
        // Lantern bodies: a bright warm dot drawn with tint > 1. The scene
        // target is a float texture, so that isn't clamped yet — it stays
        // "hotter than white" and is what bloom will pick up next stage.
        foreach (var pos in _lanterns)
            _particleSprite.Draw(pos, new Vector2D<float>(18f, 18f), _projection, new Vector4(LanternColor * 3f, 1f));

        foreach (var platform in _platforms)
            _platformSprite.Draw(platform.Center, platform.Size, _projection);

        foreach (var target in _targets)
        {
            // Hit flash: briefly multiply the texture color above 1.0, which
            // the GPU clamps to white — a cheap "just got hit" pulse using
            // the same uTint uniform particles use for fading, no separate
            // flash shader needed.
            float flash = target.HitFlashT;
            var tint = new Vector4(1f + flash * 3f, 1f + flash * 3f, 1f + flash * 3f, 1f);

            // Same bottom-anchoring trick as the player's squash/stretch:
            // VisualScale only changes the draw size, never target.Size (the
            // real collision box), and the quad's bottom edge stays pinned to
            // target.Position.Y so squashing reads as compressing in place
            // instead of sinking through the floor.
            var targetVisualSize = new Vector2D<float>(
                target.Size.X * target.VisualScale.X,
                target.Size.Y * target.VisualScale.Y);
            var targetCenter = new Vector2D<float>(
                target.Position.X + target.Size.X / 2f,
                target.Position.Y + targetVisualSize.Y / 2f);
            _targetSprite.Draw(targetCenter, targetVisualSize, _projection, tint);
        }

        _slashParticles.Draw(_slashSprite, _projection);

        // Placeholder swing visual: there's no attack animation/weapon sprite
        // yet, so the active hitbox itself is drawn as a soft translucent
        // flash — a stand-in until Ep6 gets a real attack sprite.
        if (_player.IsAttacking)
        {
            var hitbox = _player.GetAttackHitbox();
            float alpha = _player.IsAttackHitboxActive ? 0.6f : 0.25f;
            _particleSprite.Draw(hitbox.Center, hitbox.Size, _projection, new Vector4(1f, 1f, 1f, alpha));
        }

        // Squash/stretch only ever changes the drawn size, never Player.Size
        // (the actual collision box) — bottom-anchored so squashing reads as
        // sinking toward the feet instead of floating or clipping through
        // the floor.
        var visualSize = new Vector2D<float>(
            _player.Size.X * _player.VisualScale.X,
            _player.Size.Y * _player.VisualScale.Y);
        var playerCenter = new Vector2D<float>(
            _player.Position.X + _player.Size.X / 2f,
            _player.Position.Y + visualSize.Y / 2f);
        _playerSprite.Draw(playerCenter, visualSize, _projection);

        _dustParticles.Draw(_particleSprite, _projection);
    }

    private void OnResize(Vector2D<int> size)
    {
        _windowSize = size;
        UpdateProjection();
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _framebufferSize = size;
        _sceneTarget.Resize(size.X, size.Y);
        _lighting.Resize(size.X, size.Y);
    }

    private void UpdateProjection()
    {
        // World space: origin bottom-left, X right, Y up, 1 unit = 1 pixel —
        // ordinary screen-coordinate thinking, unlike raw GL clip space.
        _projection = Matrix4x4.CreateOrthographicOffCenter(0, _windowSize.X, 0, _windowSize.Y, -1f, 1f);
    }

    private void OnClosing()
    {
        _playerSprite.Dispose();
        _platformSprite.Dispose();
        _particleSprite.Dispose();
        _targetSprite.Dispose();
        _slashSprite.Dispose();
        _playerTexture.Dispose();
        _platformTexture.Dispose();
        _particleTexture.Dispose();
        _targetTexture.Dispose();
        _slashTexture.Dispose();
        _spriteShader.Dispose();
        _lighting.Dispose();
        _sceneTarget.Dispose();
        _fullscreenQuad.Dispose();
        _lightShader.Dispose();
        _compositeShader.Dispose();
        _input.Dispose();
    }
}
