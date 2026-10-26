using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Engine.Fps;

// The FPS mode's window: reads input, hands it to FpsWorld (which owns all
// the game rules and state), then draws that state in two halves:
//
//   1. the 3D world  — perspective camera, depth buffer, level meshes,
//                      billboard enemies/projectiles/particles, dynamic lights
//   2. the 2D HUD    — the same Sprite + orthographic projection the
//                      platformer uses: gun, crosshair, bars, weapon slots
//
// It opens on a title screen (FpsMenu) with the arena slowly turning behind
// it; the world doesn't Update while any menu is open, so pausing really
// freezes the game.
//
// Controls: mouse look, WASD move, Space jump (hold W along a wall in the air
// to wall-run, Space again to wall-jump), Shift sprint, left click shoot,
// 1-4 / mouse wheel switch weapon, R reload, Esc pause menu, Enter restart
// after dying (Esc then goes back to the title screen).
public sealed class FpsWindow
{
    private readonly IWindow _window;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private IMouse _mouse = null!;

    // ---- 3D rendering
    private Shader _worldShader = null!;     // world.vert + world.frag (level meshes)
    private Shader _billboardShader = null!; // billboard.vert + world.frag (everything sprite-like)
    private BillboardRenderer _billboards = null!;
    private readonly Dictionary<char, (Mesh Mesh, Texture Texture)> _walls = new();
    private Mesh _floorMesh = null!, _ceilingMesh = null!;
    private Texture _floorTexture = null!, _ceilingTexture = null!, _particleTexture = null!;
    private readonly Dictionary<EnemyKind, Texture> _enemyTextures = new();
    private readonly List<DynamicLight> _frameLights = new();
    private const int MaxLights = 8; // must match MAX_LIGHTS in world.frag

    // ---- 2D HUD
    private Shader _spriteShader = null!;
    private Texture _whiteTexture = null!;
    private Sprite _muzzleSprite = null!, _whiteSprite = null!;
    private readonly Dictionary<WeaponDefinition, (Texture Texture, Sprite Sprite)> _viewModels = new();
    private Shader _textShader = null!;      // text.vert + sprite.frag
    private TextRenderer _text = null!;

    // ---- menus
    private readonly FpsMenu _menu = new();
    private bool _menuOpen = true;           // the game starts on the title screen
    private bool _runInProgress;             // false = title screen, true = a run exists (playing or paused)
    private float _menuCameraYaw;            // the title screen's slowly turning view
    private int _bestWave, _bestKills;       // this session only — nothing is saved to disk yet

    // Fog colour doubles as the clear colour, so the far distance fades
    // seamlessly into "nothing" instead of ending at a hard edge.
    private static readonly Vector3 FogColor = new(0.03f, 0.035f, 0.06f);
    private const float FogStart = 3f, FogEnd = 15f, LightFalloff = 0.035f;
    private const float BaseFieldOfViewDegrees = 75f;
    private const float MaxSpeedFovKickDegrees = 12f;

    // ---- game
    private FpsLevel _level = null!;
    private FpsWorld _world = null!;

    // ---- input
    private bool _mouseCaptured;
    private Vector2? _lastMousePosition;
    private bool _fireHeld;
    // Semi-auto guns fire on a click, not a hold. A click that lands during
    // the cooldown is remembered briefly (like the platformer's jump buffer)
    // so fast clicking doesn't silently eat shots.
    private float _fireBuffer;
    private const float FireBufferTime = 0.15f;

    // ---- feedback
    private float _damageFlash;   // 1 -> 0 red screen tint after getting hit
    private float _hitMarker;     // 1 -> 0 crosshair turns red after landing a hit
    private float _waveBanner;    // 1 -> 0 brief screen pulse when a wave starts
    private float _fovKick;       // smoothed extra FOV from going fast

    // ---- title-bar HUD (no text rendering yet, so stats live here)
    private float _titleTimer;
    private int _framesThisSecond;
    private float _fpsTimer;
    private int _framesPerSecond;

    private Vector2D<int> _windowSize;
    private Matrix4x4 _hudProjection;

    public FpsWindow(string title = "My Engine - FPS", int width = 1280, int height = 720)
    {
        _windowSize = new Vector2D<int>(width, height);
        var options = WindowOptions.Default with
        {
            Size = _windowSize,
            Title = title,
            // 3D needs a depth buffer: per pixel, how far away the closest
            // thing drawn there so far is, so far walls don't paint over near ones.
            PreferredDepthBufferBits = 24,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Resize += OnResize;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Closing += OnClosing;
    }

    public void Run() => _window.Run();

    // ===================================================================== load

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        _mouse = _input.Mice[0];

        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

        _worldShader = new Shader(_gl, Path.Combine(shaderDir, "world.vert"), Path.Combine(shaderDir, "world.frag"));
        _billboardShader = new Shader(_gl, Path.Combine(shaderDir, "billboard.vert"), Path.Combine(shaderDir, "world.frag"));
        _spriteShader = new Shader(_gl, Path.Combine(shaderDir, "sprite.vert"), Path.Combine(shaderDir, "sprite.frag"));
        _textShader = new Shader(_gl, Path.Combine(shaderDir, "text.vert"), Path.Combine(shaderDir, "sprite.frag"));
        _text = new TextRenderer(_gl, _textShader);
        _billboards = new BillboardRenderer(_gl, _billboardShader);

        _level = new FpsLevel(FpsLevel.DefaultMap);
        _walls['#'] = (_level.BuildWallMesh(_gl, '#')!, ProceduralTextures.Brick(_gl));
        _walls['S'] = (_level.BuildWallMesh(_gl, 'S')!, ProceduralTextures.Stone(_gl));
        _floorMesh = _level.BuildFlatMesh(_gl, 0f, shade: 0.95f);
        _ceilingMesh = _level.BuildFlatMesh(_gl, FpsLevel.WallHeight, shade: 0.75f);
        _floorTexture = ProceduralTextures.Floor(_gl);
        _ceilingTexture = ProceduralTextures.Ceiling(_gl);
        _particleTexture = new Texture(_gl, Path.Combine(assetsDir, "particle.png")); // reused from the platformer

        _enemyTextures[EnemyKind.Imp] = ProceduralTextures.Imp(_gl);
        _enemyTextures[EnemyKind.Spitter] = ProceduralTextures.Spitter(_gl);
        _enemyTextures[EnemyKind.Brute] = ProceduralTextures.Brute(_gl);
        _enemyTextures[EnemyKind.Wisp] = ProceduralTextures.Wisp(_gl);

        _whiteTexture = ProceduralTextures.White(_gl);
        _muzzleSprite = new Sprite(_gl, _spriteShader, _particleTexture);
        _whiteSprite = new Sprite(_gl, _spriteShader, _whiteTexture);
        foreach (var definition in WeaponDefinition.All)
        {
            var texture = definition.CreateTexture(_gl);
            _viewModels[definition] = (texture, new Sprite(_gl, _spriteShader, texture));
        }

        _world = new FpsWorld(_level);
        _world.PlayerDamaged += _ => _damageFlash = 1f;
        _world.EnemyHit += () => _hitMarker = 1f;
        _world.WaveStarted += _ => _waveBanner = 1f;
        _world.WeaponFired += _ => _fireBuffer = 0f;

        _keyboard.KeyDown += OnKeyDown;
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += (_, button) => { if (button == MouseButton.Left) _fireHeld = false; };
        _mouse.MouseMove += OnMouseMove;
        _mouse.Scroll += (_, wheel) =>
        {
            if (wheel.Y != 0f && !_menuOpen && _world.CurrentPhase != FpsWorld.Phase.Dead)
                _world.Arsenal.Cycle(wheel.Y > 0f ? -1 : 1);
        };
        // Alt-tabbing away mid-fight pauses instead of letting you die in the background.
        _window.FocusChanged += focused =>
        {
            if (!focused && !_menuOpen && _world.Player.IsAlive) OpenPauseMenu();
        };

        UpdateHudProjection();
        OpenMenu(FpsMenu.Page.Main);
    }

    // ===================================================================== menus

    private void OpenMenu(FpsMenu.Page page)
    {
        _menuOpen = true;
        _menu.Open(page);
        SetMouseCaptured(false); // menus need a visible cursor
    }

    private void OpenPauseMenu()
    {
        _menu.StatusLine = $"WAVE {_world.Wave} - {_world.Kills} KILLS";
        OpenMenu(FpsMenu.Page.Pause);
    }

    private void OpenMainMenu()
    {
        RecordRun();
        _world.Reset();          // clean, empty arena behind the title screen
        _runInProgress = false;
        _menu.StatusLine = _bestWave > 0 ? $"BEST THIS SESSION: WAVE {_bestWave} - {_bestKills} KILLS" : "";
        OpenMenu(FpsMenu.Page.Main);
    }

    private void StartRun()
    {
        RecordRun();
        _world.Reset();
        _damageFlash = _hitMarker = _waveBanner = _fovKick = 0f;
        _runInProgress = true;
        CloseMenu();
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        SetMouseCaptured(true);
    }

    private void RecordRun()
    {
        if (!_runInProgress) return;
        // Best wave wins; kills only break a tie.
        if (_world.Wave > _bestWave || (_world.Wave == _bestWave && _world.Kills > _bestKills))
            (_bestWave, _bestKills) = (_world.Wave, _world.Kills);
    }

    private void Execute(MenuCommand command)
    {
        switch (command)
        {
            case MenuCommand.Play:
            case MenuCommand.Restart:
                StartRun();
                break;
            case MenuCommand.Resume:
                CloseMenu();
                break;
            case MenuCommand.MainMenu:
                OpenMainMenu();
                break;
            case MenuCommand.Quit:
                _window.Close();
                break;
        }
    }

    // Silk reports the mouse with Y going DOWN from the top-left corner; the
    // HUD's projection has Y going UP from the bottom-left. Flip once here.
    private Vector2 MouseInHudSpace(Vector2 position) => new(position.X, _windowSize.Y - position.Y);

    private Vector2 ScreenSize => new(_windowSize.X, _windowSize.Y);

    // ===================================================================== input

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (_menuOpen)
        {
            switch (key)
            {
                case Key.Up or Key.W: _menu.MoveSelection(-1); break;
                case Key.Down or Key.S: _menu.MoveSelection(1); break;
                case Key.Enter or Key.KeypadEnter or Key.Space: Execute(_menu.Confirm()); break;
                case Key.Escape: Execute(_menu.Back()); break;
            }
            return;
        }

        bool playing = _world.CurrentPhase != FpsWorld.Phase.Dead;
        switch (key)
        {
            case Key.Escape:
                if (playing) OpenPauseMenu();
                else OpenMainMenu();
                break;
            case Key.Space when playing:
                _world.Player.BufferJump();
                break;
            case Key.R when playing:
                _world.Arsenal.Current.StartReload();
                break;
            case >= Key.Number1 and <= Key.Number4 when playing:
                _world.Arsenal.SwitchTo(key - Key.Number1);
                break;
            case Key.Enter when !playing:
                StartRun();
                break;
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left) return;

        if (_menuOpen)
        {
            if (_lastMousePosition is { } position)
                Execute(_menu.Click(MouseInHudSpace(position), ScreenSize));
            return;
        }

        // The first click after the mouse was released (e.g. by alt-tab) just grabs the mouse again — it shouldn't
        // also fire a shot you didn't mean to take.
        if (!_mouseCaptured) { SetMouseCaptured(true); return; }
        _fireHeld = true;
        _fireBuffer = FireBufferTime;
    }

    // Raw mouse deltas: the cursor is hidden and locked, and we only care how
    // far it moved since last event, turned into yaw/pitch.
    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_menuOpen)
            _menu.Hover(MouseInHudSpace(position), ScreenSize);
        else if (_mouseCaptured && _lastMousePosition is { } last && _world.Player.IsAlive)
        {
            Vector2 delta = position - last;
            _world.Player.Look(delta.X, delta.Y);
        }
        _lastMousePosition = position;
    }

    private void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        var cursor = _mouse.Cursor;
        if (captured)
            // Raw = unaccelerated hardware motion (what shooters want) where
            // supported; Disabled = hidden + locked, as a fallback.
            cursor.CursorMode = cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled;
        else
            cursor.CursorMode = CursorMode.Normal;

        // Switching modes can make the reported position jump; forget the old
        // one so that jump isn't read as a giant mouse flick.
        _lastMousePosition = null;
        _fireHeld = false;
    }

    // ===================================================================== update

    private void OnUpdate(double deltaTime)
    {
        float dt = (float)Math.Min(deltaTime, 0.05); // don't let a hitch teleport anything

        if (_menuOpen)
        {
            // Nothing in the world moves; only the menu animates and, on the
            // title screen, the camera slowly turns.
            _menu.Update(dt);
            _menuCameraYaw += dt * 0.12f;
            UpdateTitle(dt);
            return;
        }

        _damageFlash = MathF.Max(0f, _damageFlash - dt * 2.5f);
        _hitMarker = MathF.Max(0f, _hitMarker - dt * 6f);
        _waveBanner = MathF.Max(0f, _waveBanner - dt * 1.5f);
        _fireBuffer = MathF.Max(0f, _fireBuffer - dt);

        bool automatic = _world.Arsenal.Current.Definition.Automatic;
        bool fireRequested = automatic ? _fireHeld : _fireBuffer > 0f;

        _world.Update(dt, ReadMoveInput(), _keyboard.IsKeyPressed(Key.ShiftLeft), fireRequested);

        // Speed FOV kick: going faster than a normal run (wall-running, rocket
        // jumps) widens the view a little — a cheap but strong sense of speed.
        var player = _world.Player;
        float overspeed = Math.Clamp((player.HorizontalSpeed - player.MoveSpeed) / 4f, 0f, 1f);
        float targetKick = MathF.Max(overspeed, player.IsWallRunning ? 0.6f : 0f) * MaxSpeedFovKickDegrees;
        _fovKick += (targetKick - _fovKick) * (1f - MathF.Exp(-6f * dt));

        UpdateTitle(dt);
    }

    private Vector2 ReadMoveInput()
    {
        var input = Vector2.Zero;
        if (_keyboard.IsKeyPressed(Key.W) || _keyboard.IsKeyPressed(Key.Up)) input.Y += 1f;
        if (_keyboard.IsKeyPressed(Key.S) || _keyboard.IsKeyPressed(Key.Down)) input.Y -= 1f;
        if (_keyboard.IsKeyPressed(Key.D) || _keyboard.IsKeyPressed(Key.Right)) input.X += 1f;
        if (_keyboard.IsKeyPressed(Key.A) || _keyboard.IsKeyPressed(Key.Left)) input.X -= 1f;
        return input;
    }

    // ===================================================================== render

    private void OnRender(double deltaTime)
    {
        _framesThisSecond++;

        _gl.ClearColor(FogColor.X, FogColor.Y, FogColor.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Title screen: look around the empty arena from the spawn point with
        // a slow turn. Otherwise (playing or paused): the player's own view.
        var player = _world.Player;
        if (_runInProgress)
        {
            DrawWorld(player.EyePosition, player.ViewForward, player.ViewRoll);
            DrawHud();
        }
        else
        {
            const float pitch = 0.08f;
            var forward = new Vector3(MathF.Sin(_menuCameraYaw) * MathF.Cos(pitch), MathF.Sin(pitch),
                -MathF.Cos(_menuCameraYaw) * MathF.Cos(pitch));
            DrawWorld(player.EyePosition, forward, 0f);
        }

        if (_menuOpen)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.Blend);
            _menu.Draw(_text, _whiteSprite, _hudProjection, ScreenSize);
        }
    }

    private void DrawWorld(Vector3 eye, Vector3 forward, float roll)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        // Back-face culling stays off: with no culling, wall winding order
        // can't make faces vanish, and the level is tiny enough not to care.
        _gl.Disable(EnableCap.CullFace);

        // Camera roll (the wall-run head tilt): rotate the "up" direction
        // around the look direction before building the view matrix.
        Vector3 up = Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(forward, roll));
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + forward, up);

        float aspect = _windowSize.Y > 0 ? (float)_windowSize.X / _windowSize.Y : 1f;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            (BaseFieldOfViewDegrees + _fovKick) * MathF.PI / 180f, aspect, 0.05f, 100f);

        var weapon = _world.Arsenal.Current;
        float muzzleLight = weapon.FlashT * weapon.Definition.MuzzleLight;
        _world.CollectLights(_frameLights);

        // --- level geometry (opaque)
        ApplySceneUniforms(_worldShader, view, projection, muzzleLight);
        _worldShader.SetUniform("uTint", Vector4.One);
        _worldShader.SetUniform("uAlphaCutoff", 0f);
        _worldShader.SetUniform("uEmissive", 0f);

        _floorTexture.Bind();
        _floorMesh.Draw();
        _ceilingTexture.Bind();
        _ceilingMesh.Draw();
        foreach (var (mesh, texture) in _walls.Values)
        {
            texture.Bind();
            mesh.Draw();
        }

        // --- enemies (alpha cutout: still opaque as far as depth is concerned)
        ApplySceneUniforms(_billboardShader, view, projection, muzzleLight);
        // Billboards face the camera: their "right" is the camera's right,
        // flattened onto the ground (forward turned 90° around the Y axis).
        var flatRight = Vector3.Normalize(new Vector3(-forward.Z, 0f, forward.X));
        _billboardShader.SetUniform("uCameraRight", flatRight);
        _billboardShader.SetUniform("uAlphaCutoff", 0.5f);

        foreach (var enemy in _world.Enemies)
        {
            // Wisps glow (partly ignore lighting) so they read in the dark.
            _billboardShader.SetUniform("uEmissive", enemy.Kind == EnemyKind.Wisp && enemy.IsAlive ? 0.6f : 0f);
            var size = new Vector2(enemy.Width * enemy.VisualScale.X, enemy.Height * enemy.VisualScale.Y);
            var basePosition = new Vector3(enemy.Position.X, enemy.BaseY + enemy.WalkBob, enemy.Position.Y);
            _billboards.Draw(_enemyTextures[enemy.Kind], basePosition, size, EnemyTint(enemy));
        }

        // --- projectiles + particles (translucent: blended, drawn last, and
        // they don't write depth so overlapping soft dots don't cut holes in
        // each other)
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _billboardShader.SetUniform("uAlphaCutoff", 0.01f);
        _billboardShader.SetUniform("uEmissive", 1f);

        foreach (var projectile in _world.Projectiles)
        {
            var basePosition = projectile.Position - new Vector3(0f, projectile.Size * 0.5f, 0f);
            _billboards.Draw(_particleTexture, basePosition, new Vector2(projectile.Size, projectile.Size),
                new Vector4(projectile.Color * 1.5f, 1f));
        }
        _world.Particles.Draw(_billboards, _particleTexture);
        _gl.DepthMask(true);
    }

    private void ApplySceneUniforms(Shader shader, Matrix4x4 view, Matrix4x4 projection, float muzzleLight)
    {
        shader.Use();
        shader.SetUniform("uView", view);
        shader.SetUniform("uProjection", projection);
        shader.SetUniform("uTexture", 0);
        shader.SetUniform("uFogColor", FogColor);
        shader.SetUniform("uFogStart", FogStart);
        shader.SetUniform("uFogEnd", FogEnd);
        shader.SetUniform("uLightFalloff", LightFalloff);
        shader.SetUniform("uMuzzleFlash", muzzleLight);

        // Uniform arrays are set one element at a time, by name: "uLightPositions[3]".
        int count = Math.Min(_frameLights.Count, MaxLights);
        shader.SetUniform("uLightCount", count);
        for (int i = 0; i < count; i++)
        {
            var light = _frameLights[i];
            shader.SetUniform($"uLightPositions[{i}]", light.Position);
            shader.SetUniform($"uLightColors[{i}]", light.Color * light.Intensity);
            shader.SetUniform($"uLightRadii[{i}]", light.Radius);
        }
    }

    private static Vector4 EnemyTint(Enemy enemy)
    {
        if (!enemy.IsAlive) return new Vector4(0.45f, 0.4f, 0.45f, 1f);

        // Telegraph: ramp toward an angry red glow during any windup...
        float windup = enemy.WindupT;
        var tint = new Vector4(1f + windup * 1.5f, 1f - windup * 0.4f, 1f - windup * 0.4f, 1f);
        // ...a dazed blue-grey while a Brute is stunned (your opening)...
        if (enemy is Brute { IsStunned: true }) tint = new Vector4(0.7f, 0.8f, 1.3f, 1f);
        // ...and flash bright white when shot (values > 1 brighten the texel,
        // the same trick as the platformer's Target).
        float flash = 1f + enemy.HitFlashT * 3f;
        return tint * new Vector4(flash, flash, flash, 1f);
    }

    // The HUD is plain 2D: depth testing off, the same sprite shader and
    // pixel-space orthographic projection as the platformer.
    private void DrawHud()
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);

        float w = _windowSize.X, h = _windowSize.Y;
        var center = new Vector2D<float>(w / 2f, h / 2f);
        var phase = _world.CurrentPhase;

        if (phase != FpsWorld.Phase.Dead) DrawGun(w);
        DrawCrosshair(center);
        DrawHealthBar();
        DrawAmmo(w);
        DrawWeaponSlots(w);

        if (_waveBanner > 0f)
            _whiteSprite.Draw(center, new(w, h), _hudProjection, new Vector4(1f, 0.85f, 0.5f, 0.12f * _waveBanner));
        if (_damageFlash > 0f)
            _whiteSprite.Draw(center, new(w, h), _hudProjection, new Vector4(0.8f, 0f, 0f, 0.35f * _damageFlash));
        if (phase == FpsWorld.Phase.Dead && !_menuOpen)
            DrawDeathScreen(center, w, h);
    }

    private void DrawDeathScreen(Vector2D<float> center, float w, float h)
    {
        _whiteSprite.Draw(center, new(w, h), _hudProjection, new Vector4(0.35f, 0f, 0f, 0.55f));

        int unit = Math.Max(1, (int)(h / 360f)); // same sizing rule as FpsMenu
        _text.Draw("YOU DIED", new(center.X, h * 0.58f), 8 * unit, new Vector4(1f, 0.25f, 0.2f, 1f),
            _hudProjection, TextAlign.Center);
        _text.Draw($"WAVE {_world.Wave} - {_world.Kills} KILLS", new(center.X, h * 0.58f - 18f * unit), 3 * unit,
            new Vector4(1f, 0.85f, 0.75f, 1f), _hudProjection, TextAlign.Center);
        _text.Draw("ENTER: TRY AGAIN    ESC: MAIN MENU", new(center.X, h * 0.3f), 2 * unit,
            new Vector4(0.9f, 0.8f, 0.8f, 1f), _hudProjection, TextAlign.Center);
    }

    private void DrawGun(float screenWidth)
    {
        var player = _world.Player;
        var arsenal = _world.Arsenal;
        var weapon = arsenal.Current;
        var definition = weapon.Definition;

        float size = MathF.Min(screenWidth, _windowSize.Y * 1.6f) * 0.28f * definition.ViewScale;

        // Sway follows the head bob (figure-8: X at half the frequency of Y,
        // like the hand swinging with each step), the kick shoves it down
        // after each shot, and reloading/switching dips it out of view and back.
        float swayX = MathF.Sin(player.BobPhase * 0.5f) * 12f * player.Speed01;
        float swayY = -MathF.Abs(MathF.Cos(player.BobPhase * 0.5f)) * 10f * player.Speed01;
        // While airborne, the gun lags a bit behind vertical motion (rises as you fall).
        float airLag = Math.Clamp(-player.VerticalVelocity * 3f, -25f, 25f);
        // Wall-running: tilt the gun with the head and pull it slightly inward.
        float rollShift = player.ViewRoll * 120f;
        float kick = Easing.OutCubic(weapon.KickT) * size * 0.08f;
        float reloadDip = MathF.Sin(weapon.ReloadProgress * MathF.PI) * size * 0.6f;
        float switchDip = arsenal.LowerAmount * size * 0.9f;

        float centerX = screenWidth / 2f + swayX + rollShift;
        float bottom = -size * 0.05f + swayY + airLag - kick - reloadDip - switchDip;
        var sprite = _viewModels[definition].Sprite;
        sprite.Draw(new(centerX, bottom + size / 2f), new(size, size), _hudProjection, rotation: player.ViewRoll * 0.6f);

        if (weapon.FlashT > 0f)
        {
            float flashSize = size * 0.55f * (0.8f + 0.4f * weapon.FlashT) * definition.MuzzleLight;
            var flashPos = new Vector2D<float>(centerX, bottom + size * definition.MuzzleHeight);
            _muzzleSprite.Draw(flashPos, new(flashSize, flashSize), _hudProjection,
                new Vector4(1.5f, 1.1f, 0.5f, weapon.FlashT), rotation: weapon.FlashT * 2f);
        }
    }

    private void DrawCrosshair(Vector2D<float> c)
    {
        var weapon = _world.Arsenal.Current;
        var color = Vector4.Lerp(new Vector4(1f, 1f, 1f, 0.8f), new Vector4(1f, 0.15f, 0.15f, 1f), _hitMarker);
        // Wider base gap for spread-y weapons, so the crosshair hints at accuracy.
        float gap = 5f + weapon.Definition.Spread * 120f + weapon.KickT * 6f;
        const float length = 10f, thickness = 2f;

        _whiteSprite.Draw(new(c.X - gap - length / 2f, c.Y), new(length, thickness), _hudProjection, color);
        _whiteSprite.Draw(new(c.X + gap + length / 2f, c.Y), new(length, thickness), _hudProjection, color);
        _whiteSprite.Draw(new(c.X, c.Y - gap - length / 2f), new(thickness, length), _hudProjection, color);
        _whiteSprite.Draw(new(c.X, c.Y + gap + length / 2f), new(thickness, length), _hudProjection, color);
    }

    private void DrawHealthBar()
    {
        const float x = 24f, y = 24f, width = 220f, height = 16f;
        var player = _world.Player;
        float fraction = (float)player.Health / player.MaxHealth;

        _whiteSprite.Draw(new(x + width / 2f, y + height / 2f), new(width + 4f, height + 4f), _hudProjection,
            new Vector4(0f, 0f, 0f, 0.6f));
        // Sprites are positioned by their centre, so a bar that shrinks from
        // the right needs its centre at x + filledWidth/2.
        float filled = width * fraction;
        var color = Vector4.Lerp(new Vector4(0.9f, 0.15f, 0.1f, 1f), new Vector4(0.3f, 0.85f, 0.3f, 1f), fraction);
        _whiteSprite.Draw(new(x + filled / 2f, y + height / 2f), new(filled, height), _hudProjection, color);
    }

    private void DrawAmmo(float screenWidth)
    {
        var weapon = _world.Arsenal.Current;
        int magazine = weapon.Definition.MagazineSize;
        const float pipHeight = 20f, y = 24f, maxRowWidth = 280f;
        // Shrink the pips for big magazines so the row never gets too long.
        float spacing = MathF.Min(14f, maxRowWidth / magazine);
        float pipWidth = spacing * 0.65f;
        float right = screenWidth - 24f;

        // While reloading, pips refill one by one with the reload progress.
        int shown = weapon.IsReloading ? (int)(weapon.ReloadProgress * magazine) : weapon.Ammo;

        for (int i = 0; i < magazine; i++)
        {
            float x = right - i * spacing - pipWidth / 2f;
            var color = i < shown ? new Vector4(1f, 0.85f, 0.4f, 1f) : new Vector4(1f, 1f, 1f, 0.15f);
            _whiteSprite.Draw(new(x, y + pipHeight / 2f), new(pipWidth, pipHeight), _hudProjection, color);
        }
    }

    // Four small boxes above the ammo: which slot (1-4) is selected.
    private void DrawWeaponSlots(float screenWidth)
    {
        const float boxWidth = 30f, boxHeight = 8f, spacing = 36f, y = 56f;
        var arsenal = _world.Arsenal;
        int count = arsenal.Weapons.Count;
        float left = screenWidth - 24f - count * spacing + (spacing - boxWidth);

        for (int i = 0; i < count; i++)
        {
            bool selected = i == arsenal.CurrentIndex;
            var color = selected ? new Vector4(1f, 0.85f, 0.4f, 1f) : new Vector4(1f, 1f, 1f, 0.25f);
            float x = left + i * spacing + boxWidth / 2f;
            _whiteSprite.Draw(new(x, y + boxHeight / 2f), new(boxWidth, selected ? boxHeight * 1.5f : boxHeight),
                _hudProjection, color);
        }
    }

    // ===================================================================== misc

    private void UpdateTitle(float dt)
    {
        _fpsTimer += dt;
        if (_fpsTimer >= 1f)
        {
            _framesPerSecond = _framesThisSecond;
            _framesThisSecond = 0;
            _fpsTimer -= 1f;
        }

        // Setting the title is an OS call; a few times a second is plenty.
        _titleTimer -= dt;
        if (_titleTimer > 0f) return;
        _titleTimer = 0.25f;

        var weapon = _world.Arsenal.Current;
        string ammo = weapon.IsReloading ? "reloading" : $"{weapon.Ammo}/{weapon.Definition.MagazineSize}";
        string status = _world.CurrentPhase switch
        {
            FpsWorld.Phase.Dead => $"YOU DIED on wave {_world.Wave} with {_world.Kills} kills - press Enter to restart",
            FpsWorld.Phase.Intermission when _world.Wave == 0 => $"Get ready... {_world.IntermissionTimer:0.0}",
            FpsWorld.Phase.Intermission => $"Wave {_world.Wave} cleared! Next wave in {_world.IntermissionTimer:0.0}  |  HP {_world.Player.Health}",
            _ => $"Wave {_world.Wave}  |  HP {_world.Player.Health}  |  {weapon.Definition.Name} {ammo}  |  Enemies {_world.EnemiesAlive}  |  Kills {_world.Kills}",
        };
        if (_menuOpen) status = _runInProgress ? "Paused" : "Main menu";
        _window.Title = $"My Engine - FPS  |  {status}  |  {_framesPerSecond} fps";
    }

    private void OnResize(Vector2D<int> size)
    {
        _windowSize = size;
        UpdateHudProjection();
    }

    // Identical to the platformer's camera: 1 unit = 1 screen pixel. (The 3D
    // perspective projection is rebuilt every frame instead, because the
    // speed FOV kick changes it continuously.)
    private void UpdateHudProjection() =>
        _hudProjection = Matrix4x4.CreateOrthographicOffCenter(0, _windowSize.X, 0, _windowSize.Y, -1, 1);

    private void OnClosing()
    {
        foreach (var (mesh, texture) in _walls.Values)
        {
            mesh.Dispose();
            texture.Dispose();
        }
        foreach (var texture in _enemyTextures.Values) texture.Dispose();
        foreach (var (texture, sprite) in _viewModels.Values)
        {
            sprite.Dispose();
            texture.Dispose();
        }
        _floorMesh.Dispose();
        _ceilingMesh.Dispose();
        _floorTexture.Dispose();
        _ceilingTexture.Dispose();
        _particleTexture.Dispose();
        _muzzleSprite.Dispose();
        _whiteSprite.Dispose();
        _whiteTexture.Dispose();
        _billboards.Dispose();
        _worldShader.Dispose();
        _billboardShader.Dispose();
        _spriteShader.Dispose();
        _text.Dispose();
        _textShader.Dispose();
        _input.Dispose();
    }
}
