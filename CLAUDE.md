# MyEngine — project context for Claude Code

A from-scratch **2D game engine** written in **C# / .NET 10**, rendering with
**OpenGL via Silk.NET 2.23**. Built by Eddy as a **YouTube devlog series** — so
prefer clear, teachable code and explain the "why", not just the "what". New to
low-level graphics programming, comfortable with C# and Unity.

## Solution layout

```
myengine\
├─ MyEngine.sln
├─ CLAUDE.md          ← this file
├─ Engine\            ← the reusable engine (class library)
│   ├─ Engine.csproj  ← Silk.NET packages live here; AllowUnsafeBlocks is on
│   └─ GameWindow.cs  ← window + game loop + current rendering code
└─ Game\              ← the game / entry point (console exe, references Engine)
    ├─ Game.csproj
    └─ Program.cs     ← creates a GameWindow and calls Run()
```

Rule learned the hard way: **never nest one project's folder inside another's** —
an SDK project compiles every `.cs` beneath it, which caused duplicate-attribute
build errors. Keep `Engine` and `Game` as siblings.

## Run it

```powershell
dotnet run --project Game\Game.csproj
```

## Packages (all on Engine.csproj, v2.23.0 unless noted)

Silk.NET.Windowing, Silk.NET.OpenGL, Silk.NET.Input, Silk.NET.Maths,
StbImageSharp (2.30.15, for PNG/JPG decoding in `Texture`).
`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is required — talking to the GPU
means raw pointers (`fixed`, `void*`).

Shader source (`Engine/Shaders/*.vert`, `*.frag`) and texture assets
(`Engine/Assets/*.png`) are `Content` items in `Engine.csproj` with
`CopyToOutputDirectory`. .NET SDK projects propagate `Content` items
transitively across `ProjectReference`s, so they land in `Game`'s output
directory automatically — no need to duplicate them in `Game.csproj`. Loaded
at runtime via `Path.Combine(AppContext.BaseDirectory, "Shaders", ...)` /
`"Assets"`.

## Architecture

Simple, object-oriented (not ECS, by choice). `GameWindow` owns the window and
the Load/Update/Render loop. `Update(deltaTime)` = game logic, `Render()` =
drawing. Always multiply movement by `deltaTime` so speed is frame-rate
independent. This will grow toward a `GameObject` base class with `Update`/`Draw`.

Reusable rendering classes live directly in `Engine/` (not nested under
`GameWindow`):
- `Shader` — compiles + links a vertex/fragment pair from file paths, exposes
  `Use()` and typed `SetUniform` overloads (`int`, `float`, `Vector2D<float>`,
  and `System.Numerics.Matrix4x4` for the projection). `SetUniform` must be
  called after `Use()` — `glUniform*` writes to whichever program is
  currently bound.
- `Texture` — decodes an image via StbImageSharp and uploads it as a GL
  texture; flips on load so UV (0,0) is bottom-left like every GL tutorial
  assumes; nearest-neighbor filtering (pixel art, not blurry).
- `Sprite` — a unit quad (VAO/VBO/EBO, positions + UVs) that draws itself at
  an arbitrary position/size in **world pixels** via shader uniforms, given a
  `Shader` + `Texture` + the camera's projection matrix. One `Sprite`
  instance's GPU buffers can be redrawn anywhere without re-uploading vertex
  data — this is the pattern Ep4 tilemaps will repeat.
- `Aabb` — axis-aligned bounding box (`Min` = bottom-left corner + `Size`),
  with a strict-inequality `Intersects` (touching edges don't count as
  overlapping, which matters right after collision resolution).
- `Player` — the platformer character controller (see below).
- `ParticleSystem` — a small fixed-capacity particle pool (dust bursts on
  jump/land, spark bursts on melee hits, see below).
- `Target` — a stationary melee practice dummy (health, hit-flash, hit-squash,
  `Hit`/`Died` events; see Melee attack below).
- `Easing` — one static helper, `OutCubic(t)`, shared by every "snap away
  from normal then ease back" reaction (`Player`'s landing squash, `Target`'s
  hit squash) so that curve shape lives in one place.

`Shader.SetUniform` also has a `Vector4` overload (`uTint`), and
`Sprite.Draw` takes optional `Vector4? tint` (default = opaque white) and
`float rotation` (default 0, radians) params — added so `ParticleSystem` can
tint/fade/rotate one shared texture per particle instead of needing a
separate shader or per-particle textures. `sprite.frag` multiplies the
sampled texel by `uTint`; `sprite.vert` rotates the unit quad by `uRotation`
around its own center before scaling/translating it into world space. Both
params are optional specifically so older call sites (platform sprites, the
plain player draw) don't need to change to stay correct.

Camera/projection: `GameWindow` builds an orthographic matrix
(`Matrix4x4.CreateOrthographicOffCenter(0, width, 0, height, -1, 1)`) so
world space is ordinary screen-pixel thinking — origin bottom-left, X right,
Y up, 1 unit = 1 pixel — instead of raw -1..1 GL clip space. Recomputed on
`_window.Resize`. This landed as part of the platformer work, ahead of Ep4's
tilemaps, because pixel-space positioning is a prerequisite for both.

## Platformer controller (`Player`)

Built ahead of the Ep4→Ep5 order because it was asked for directly. Lives in
`Engine/Player.cs`; all tuning constants are public fields (`MoveSpeed`,
`Acceleration`, `Deceleration`, `Gravity`, `JumpVelocity`, etc.) so they're
easy to tweak live while chasing feel.

- **Acceleration/deceleration**: horizontal velocity eases toward
  `moveInput * MoveSpeed` via a `MoveTowards`-style ramp (`Acceleration` when
  there's input, `Deceleration` when there isn't) instead of snapping —
  same idea as damping a Unity `Rigidbody2D.velocity` in `Update`.
  `AirControlMultiplier` softens both rates while airborne.
- **Coyote time** (`CoyoteTime`, default 0.1s): a countdown that resets to
  full every grounded frame and drains once you walk off a ledge — jumping
  is still allowed while it's > 0.
- **Jump buffering** (`JumpBufferTime`, default 0.12s): pressing jump starts
  a countdown (`BufferJump()`, wired to `KeyDown` in `GameWindow`); a jump
  fires the instant both the buffer and coyote timer are simultaneously > 0,
  so a press slightly before landing isn't dropped.
- **Jump cut** (`JumpCutMultiplier`): releasing jump early while still rising
  chops the upward velocity (`CutJumpIfRising()`, wired to `KeyUp`) for a
  variable-height short hop.
- **Collision**: gravity integrates every frame (clamped to `MaxFallSpeed`),
  then X and Y movement are applied and collided against the level's
  `List<Aabb>` **separately** (X pass, then Y pass) — resolving one axis at a
  time is what stops a fast diagonal fall from tunnelling through a corner
  that a single combined-axis check would miss. `IsGrounded` is recomputed
  every frame from the Y-axis pass, not cached.

Input: `GameWindow` polls `IKeyboard.IsKeyPressed` each `Update` for
continuous movement (A/D or Left/Right), and subscribes to `KeyDown`/`KeyUp`
for jump (Space/W/Up) since buffering needs a press *edge*, not a held state.

Level geometry is currently a handful of hand-placed `Aabb` rectangles built
in `GameWindow.BuildLevel()` (ground + four floating platforms) — real
tile-grid loading is still Ep4, not done.

## Squash &amp; stretch, and particles (Ep6 polish, done early)

Both are purely cosmetic — neither touches `Player.Position`/`Size`, the
actual collision box, so tuning or disabling either can't change gameplay.

- **Squash &amp; stretch** lives in `Player.VisualScale` (a `Vector2D<float>`
  multiplier, `(1,1)` = normal), recomputed every `Update` in
  `UpdateVisualScale`: while airborne it stretches tall/thin proportional to
  `|Velocity.Y|` (capped by `MaxAirStretch`); the frame `IsGrounded` flips
  false→true it snaps to a flat/wide squash (`LandSquashX`/`LandSquashY`) and
  eases back to `(1,1)` over `SquashDuration` with a cubic ease-out.
  `GameWindow.OnRender` multiplies `Player.Size` by `VisualScale` only for
  the draw call, and re-centers the quad so its **bottom edge stays pinned to
  `Player.Position.Y`** (the actual feet) — without that anchor a squash
  would visually sink through the floor and a stretch would float above it.
- **Particles** (`ParticleSystem`) are a fixed-capacity pool (default
  capacity 128, swap-remove on death, no per-frame allocation) drawn by
  reusing one `Sprite` instance (a soft radial-gradient dot,
  `Assets/particle.png`) with a per-particle `uTint` (color + fade-out alpha)
  and a size that lerps from `StartSize` to `EndSize` over each particle's
  life. `Player` doesn't know particles exist — it only exposes `Jumped` and
  `Landed` C# events; `GameWindow.OnLoad` subscribes to both and calls
  `_dustParticles.Emit(...)` at the player's feet (`FeetPosition()`) with
  different cone direction/spread/speed/color per event. This indirection
  (events instead of `Player` calling into `ParticleSystem` directly) keeps
  `Player` a pure gameplay/physics class with no rendering dependency.

## Melee attack &amp; targets

`Player` tracks `FacingDirection` (±1, updates only while `moveInput != 0` so
it persists while idle) and runs a small attack state machine, all in
`Player.cs`:

- `TryAttack()` (wired to `KeyDown` for J/X/Enter in `GameWindow`) starts a
  swing unless one is already in progress or `AttackCooldown` hasn't
  drained — attacks are **not** buffered like jumps; a press that arrives
  too early is just dropped, on purpose, to keep attack timing readable.
- A swing lasts `AttackDuration` seconds, but the hitbox (`GetAttackHitbox()`,
  a `44x40` `Aabb` centered `AttackRange` pixels in front of the player,
  toward `FacingDirection`) is only *live* — `IsAttackHitboxActive` true —
  between `AttackActiveStart` and `AttackActiveEnd`, a short window in the
  middle of the swing rather than its whole duration. This is the standard
  windup/active/recovery shape, just without an actual animation driving it
  yet (see the placeholder note below).
- `Player` exposes `AttackStarted` but has **no idea `Target` exists** —
  `GameWindow.OnUpdate` is what calls `GetAttackHitbox()`, tests it against
  every `Target` in `_targets`, and calls `Target.TakeDamage()` on overlap.
  `GameWindow` also owns a `HashSet&lt;Target&gt; _hitThisSwing`, cleared on
  `AttackStarted` — without it, a swing would deal damage every single frame
  its hitbox is active, not once.
- `Target.TakeDamage()` fires `Hit` (small spark burst, reusing
  `ParticleSystem`, **plus** a one-shot slash mark — see below) and, at 0
  health, `Died` (bigger 360° spark burst); dead targets are pruned from
  `_targets` at the end of the same `OnUpdate` via `RemoveAll(t => !t.IsAlive)`.
- Hit feedback reuses the same `uTint` trick as particles: `Target`'s
  `HitFlashT` (1 → 0 after each hit) is multiplied into the tint *above*
  1.0, which the GPU clamps to white — a flash pulse with no extra shader.
- **Slash mark**: `GameWindow` keeps a *second*, separate `ParticleSystem`
  (`_slashParticles`) purely because `ParticleSystem.Draw` renders every live
  particle in a pool with one shared `Sprite` — dust/sparks use
  `particle.png`, so a differently-textured effect needs its own pool rather
  than a per-particle texture id. On `Target.Hit`, one particle spawns at
  `target.Center` with zero speed (it doesn't fly anywhere) using
  `Assets/slash.png` (a soft curved arc baked opening toward +X) and sweeps
  through a small rotation arc (`startRotation`/`endRotation`, ±0.3 rad
  around a base angle) while it grows and fades over ~0.12s. Facing left
  flips the base angle by π so the arc opens the other way, since there's
  only the one asset. This is what `Particle.StartRotation`/`EndRotation`
  and the new `uRotation` uniform exist for (see above).
- **Target hit-squash**: `Target.VisualScale`, driven by `Target.Update` the
  same way `Player.VisualScale` is — on `TakeDamage`, snaps to
  `HitSquashX`/`HitSquashY` (thin along the hit axis, tall perpendicular —
  the opposite proportions from Player's flat/wide landing squash, since an
  impact compresses the axis it was hit along rather than the axis it fell
  along) and eases back to `(1,1)` over `HitSquashDuration` via
  `Easing.OutCubic`. `GameWindow.OnRender` applies the same bottom-anchoring
  as the player draw, so a squashed target compresses toward the ground
  instead of sinking through it.

**Known placeholder, called out on purpose:** there's no attack animation or
weapon sprite yet, so `GameWindow.OnRender` draws the active hitbox itself as
a soft translucent quad (reusing the particle dot sprite) purely so the swing
is visible at all. Treat this as a stand-in for real attack art, not a
design choice worth preserving.

## Cinematic lighting (staged; stage 1 of 5 done)

Chosen with Eddy: light-map FBO approach, moody dusk/night mood, built in
stages that each run on their own. Plan: (1) 2D lighting ✅, (2) bloom +
vignette + color grading/tonemap, (3) camera juice (smooth follow w/
look-ahead, screen shake, hit-stop), (4) atmosphere (parallax layers, dust
motes/fog, god rays), (5) soft 2D shadows from platforms.

Frame = 3 passes in `GameWindow.OnRender`: `Lighting.RenderLightMap` (clear
to `AmbientColor`, each `Light` drawn additively as a soft radial quad via
`light.frag`, which reuses `sprite.vert` and carries color in `uTint`) →
scene drawn into `_sceneTarget` (`DrawScene()`, unchanged sprite drawing) →
`composite.frag` on a `FullscreenQuad` outputs `scene * lightMap` to the
window. `RenderTarget` = FBO + **RGBA16F** color texture, so values > 1
survive (lantern bodies are drawn with tint×3 on purpose, for future bloom).
Render targets use `_window.FramebufferSize` (`OnFramebufferResize`);
projection still uses window size. `PointLight2D` (renamed from `Light` so it doesn't clash with the 3D `Engine.Core.Light` component) has `Flicker` and `Lifetime`
(transient flashes via `Lighting.Flash`, used on target hit/death).

## Devlog roadmap

- **Ep 1 — Foundation:** window opens, clears to navy, closes cleanly. ✅ done
- **Ep 2 — First triangle:** VAO + VBO + vertex/fragment shaders + DrawArrays.
  ✅ done — gradient triangle in `GameWindow` (see Ep3, which folds the color
  step in early).
- **Ep 3 — Color & sprites:** ✅ done. Each triangle vertex has its own color
  (gradient proving the fragment shader runs per-pixel); a PNG loads and
  draws as a textured quad via the `Shader`/`Texture`/`Sprite` classes.
- **Ep 4 — Tilemaps:** grid-based levels built from sprites. Not done — the
  platformer's level is still hand-placed `Aabb` rectangles, not loaded tile
  data. Camera/projection (needed for this too) is already in place.
- **Ep 5 — Game feel:** 2D AABB physics, gravity, collision resolution. ✅
  mostly done, done out of order (see Platformer controller above) — the
  gradient triangle demo from Ep2/3 was removed from `GameWindow` and
  replaced with the actual player/platform scene. Not yet covered: slopes,
  moving platforms, one-way platforms.
- **Ep 6 — Polish:** audio, particles, on-screen text/UI. Particles (dust on
  jump/land, sparks on melee hits) and squash-and-stretch ✅ done, out of
  order, same reason as Ep5. Audio and on-screen text/UI: not started.
- **Combat (not in the original roadmap):** melee attack + practice `Target`
  ✅ done, added directly by request. No enemy AI, no player health/damage —
  it's a one-directional "hit a dummy" loop, not a combat system yet.

## Current state

There's a playable platformer prototype: a controllable orange square with
smooth acceleration/deceleration, gravity, coyote time, jump buffering,
jump-cut, and squash-and-stretch, colliding against a small hand-built level
of green platform rectangles, kicking up dust particles on jump and landing.
It can also melee-attack (J/X/Enter) two dark-red `Target` dummies placed in
the level, which flash white, spark, and break after 3 hits. Controls: A/D or
arrow keys to move, Space/W/Up to jump, J/X/Enter to attack. Builds clean
(`dotnet build MyEngine.slnx`) and was smoke-tested via
`dotnet run --project Game/Game.csproj` (runs without exceptions; visual
correctness — actual jump feel, squash timing, particle look, attack range/
timing feel — hasn't been eyeballed in a real play session yet, so treat
"smooth"/"juicy" as implemented-per-spec rather than confirmed-fun).

Next concrete steps, in rough priority order: (1) actually playtest and tune
the feel constants (jump/squash/particle/attack tuning fields are all public
for exactly this — attack range/hitbox size in particular are pure guesses
right now), (2) a real attack animation/weapon sprite to replace the
translucent-hitbox placeholder, (3) Ep4 tile-grid level loading (replace
`BuildLevel()`'s hardcoded rectangles), (4) remaining Ep6 polish (audio,
on-screen text/UI).

## FPS mode (`Engine/Fps/`, namespace `Engine.Fps`)

A separate first-person shooter mode added by request, alongside (not
replacing) the platformer. Launch with
`dotnet run --project Game/Game.csproj -- fps`; plain `dotnet run` still
opens the platformer (`Game/Program.cs` picks based on the `fps` arg).
Full write-up: `docs/FPS-Guide.md` — keep it in sync when changing FPS code.

Wolfenstein-style design: the level is a 2D char grid
(`FpsLevel.DefaultMap`, X = column, Z = row, 1 cell = 1 unit, walls 3 tall,
`P` player spawn, `E` enemy spawn points) extruded into 3D meshes.
Collision (per-axis slide, like `Player`), bullets (grid DDA + floor/ceiling
planes), line of sight, wall-run detection (`HasWallBeside`, 4 axis normals)
and enemy pathfinding (BFS flow field, rebuilt when the player changes cell)
are all 2D on that grid; height is a separate value (`FpsPlayer.FeetY`,
`Enemy.BaseY`).

- `FpsWindow` = input + rendering only. `FpsWorld` = all game rules/state
  (waves, firing, projectiles, explosions, lights) with no GL/input, so it
  can be driven headlessly from a console app for testing.
- `FpsPlayer`: walk/jump (coyote + buffer), air control that never brakes,
  wall-run (replace — don't add to — vertical speed on latch, low gravity,
  camera roll, wall-jump), `AddImpulse` for knockback/rocket jumps.
- Weapons are data: `WeaponDefinition` (Pistol, Shotgun, SMG, Rocket
  Launcher), `Weapon` (ammo/reload state), `Arsenal` (1-4/wheel switching).
  Semi-auto clicks are buffered 0.15s in `FpsWindow`.
- Enemies: abstract `Enemy` base (health/hit/knockback/movement/sight/flow
  field/separation) + `Think()` per subclass: `Imp`, `Spitter`, `Brute`,
  `Wisp`. Wave spawns call `Alert()` (otherwise they'd idle out of sight).
- Rendering: `world.vert`/`billboard.vert` + shared `world.frag` (distance
  falloff, fog = clear color, muzzle flash, up to 8 dynamic point lights via
  uniform arrays, alpha cutout for enemies, emissive for particles).
  Distance is computed per-pixel from `vViewPosition` — a per-vertex
  distance on the giant floor quad fogged out the whole floor (fixed bug).
- All FPS art is generated in `ProceduralTextures`; only `particle.png` is
  reused. In-game stats/FPS counter still go in the window title.
- Menus: `FpsMenu` (title screen "WAVE ARENA" = `FpsMenu.GameTitle`, pause
  menu on Esc / focus loss, controls page) returns `MenuCommand`s to
  `FpsWindow`; the world doesn't update while a menu is open. Text comes from
  `TextRenderer` (hand-drawn 5x7 font baked into an atlas, `text.vert` +
  `sprite.frag`, `uUvRect` picks a glyph); uppercase only. The death screen
  uses it too. Best wave/kills are session-only (not saved).

Status: builds clean, renders correctly (screenshotted), mechanics verified
headlessly (wall-run, wall-jump, rocket jump, every enemy damages the
player, aim-bot reaches wave 11 crash-free) — not playtested for feel.

## 3D engine + Unity-style editor (`Editor\`, `Engine/Core`, `Engine/Rendering`)

Chosen with Eddy: **Unity's model** (GameObjects + Components, Unity API
names), an ImGui docking editor, our own physics, PBR, plus four "stand-out"
features. (A Godot-style node tree was built first, then replaced by request
— `Engine/Scene` no longer exists.) Staged, each stage runnable:
(1) core + editor ✅, (2) own rigidbody physics + live physics visualizer,
(3) time-rewind debugger, (4) C# hot-reload scripts ✅ (done early, see
"Scripting" below),
(5) juice toolkit +
glTF + shadow maps. Web/WebGL build + ASP.NET backend were discussed, not
started.

Run: `dotnet run --project Editor/Editor.csproj` (opens `SampleProject\`;
first run writes the demo to `SampleProject/Assets/Scenes/Main.scene`).
Flags: `--select <name>`, `--play` (presses Play on frame 30),
`--screenshot <png> --frames N` (saves the window and exits — visual checks
without synthetic input; Eddy may be using the PC meanwhile, so keep these
runs few). Standalone: `dotnet run --project Game -- scene <file.scene>`.
`SampleProject/main.scene` and `main.recovery.scene` are leftovers in the
old node format and can be deleted.

- **Core (`Engine/Core`, namespace `Engine.Core` — not `Engine.Scene`,
  since a class can't share its namespace's name)**: `EngineObject` (Unity's
  Object; `instanceId` Guid, implicit bool = "not destroyed",
  Instantiate/Destroy/DestroyImmediate/FindObjectOfType), `GameObject`
  (components, `tag`, `layer`, `SetActive`, `activeInHierarchy`,
  `GetComponent*`, `Find*`, `CreatePrimitive`, `prefabAssetPath` on instance
  roots), `Component` → `Behaviour` (`enabled`) → `MonoBehaviour` (Awake/
  OnEnable/Start/FixedUpdate/Update/LateUpdate/OnDisable/OnDestroy/
  OnValidate found by reflection, may be private, bound once as delegates;
  coroutines with WaitForSeconds/Realtime/WaitUntil/WaitWhile/nested;
  `Invoke`), `Transform` (hierarchy lives here; quaternion + remembered
  Euler hint for the Inspector; Translate/Rotate default `Space.Self`),
  `Scene` (roots + Unity lifecycle order, fixed-step accumulator,
  deferred Destroy queue, `FixedStepped` event for physics),
  `SceneManager`, `Time`, `Debug` (Log + DrawLine), `Mathf`, `Color`
  (lowercase r/g/b/a, sRGB; `ToLinear()`), `TagManager`/`LayerMask`
  (ProjectSettings/TagManager.json). `Engine.Input` gained Unity methods
  (GetKey/GetKeyDown/GetAxis("Horizontal")/GetMouseButton/mousePosition),
  keys are Silk's `Key` enum.
- **Conventions match Unity**: +Z forward, Y up, left-handed *look*. Math
  stays standard System.Numerics; the camera view matrix gets
  `* CreateScale(1,1,-1)` (as Unity's worldToCameraMatrix does), so the
  renderer sets `glFrontFace(CW)` and meshes are generated CCW-outward.
  Primitive sizes = Unity's (Capsule/Cylinder 2 m, Plane 10 m, Quad faces -Z).
  Light intensity is multiplied by π in the renderer so Unity numbers look
  similar. Vectors are still `System.Numerics` (X/Y/Z uppercase).
- **Built-in components** (`BuiltinComponents.cs`): MeshFilter (`mesh` =
  PrimitiveType), MeshRenderer ([RequireComponent(MeshFilter)], inline
  [Serializable] `Material`: color/metallic/smoothness/emissionColor/
  checker), Light (Directional/Point), Camera (fieldOfView, near/far,
  depth; `Camera.main` = tag MainCamera; render camera = highest depth),
  SkyEnvironment (our stand-in for Unity's Lighting window), Rotator example.
- **Serialization** (`SceneSerializer`, format 2, `.scene`/`.prefab` JSON):
  Unity's rule — public fields + `[SerializeField]` privates, not
  readonly/[NonSerialized]; supports primitives, enums, vectors, Color,
  [Serializable] classes/structs, List/arrays, and references to
  GameObjects/components (`{"ref": id}`, resolved in a fixup pass; prefab
  assets as `{"asset": path}`). Unknown script types load as
  `MissingComponent`, which keeps the raw JSON. Also the deep-copy used by
  Instantiate (new ids; internal refs remapped, external kept), Duplicate,
  undo and Play snapshots. Attributes: SerializeField, HideInInspector,
  Range, Header, Tooltip, AddComponentMenu, RequireComponent,
  DisallowMultipleComponent.
- **Prefabs** (`PrefabUtility`, `AssetDatabase` with project-relative
  "Assets/..." paths): rule = an instance keeps its own root name + root
  Transform; everything else comes from the .prefab. Sync happens on the
  JSON (ids matched by hierarchy path + component type/occurrence, fresh ids
  otherwise, internal refs remapped), on scene open and after Apply. Apply /
  Revert / Unpack / Select Asset. No nested-prefab sync, no per-property
  overrides (deliberately simpler than Unity).
- **Editor**: Hierarchy (drag to reparent; top/bottom edge = reorder;
  drop prefabs in; Create menus), Inspector (active/name/Tag/Layer, prefab
  bar, component headers with enable checkbox + ⋮ Reset/Move/Remove,
  reflection-driven fields incl. object reference slots with drag-drop +
  picker, lists, LayerMask, nested [Serializable] as indented rows, Add
  Component searchable by AddComponentMenu path, OnValidate after edits),
  Project (Assets tree; drag GameObject onto folder = make prefab; create
  folder/scene; delete with confirm), Console (Clear on Play), Scene view
  (EditorCamera: RMB+WASD fly, Alt+LMB orbit, MMB pan, F frame; W/E/R
  gizmo; click picks prefab root first), Game view (render camera; gets
  input only when focused during play; Play focuses it via
  SetNextWindowFocus), Tags and Layers window. Menus: File/Edit/Assets/
  GameObject/Component/Window/Help; Ctrl+P play, Ctrl+Shift+P pause.
  `EditorContext` = shared state + snapshot undo (RecordUndo before every
  edit; widgets edit copies and record on `IsItemActivated`).
  Engine has `[InternalsVisibleTo("Editor")]`. Layout file:
  `editor_layout_v2.ini` next to the exe (rename it again if panel names
  change, or old docks float). Platformer's 2D light renamed `PointLight2D`.

Status: builds clean; headless test verified Unity axis conventions,
lifecycle order, Instantiate ref remapping, deferred Destroy, coroutines,
save/load with refs/lists/private [SerializeField], prefab create/apply/
sync with unique ids. Screenshots confirm Hierarchy/Inspector/Project/
Console/Scene/Game views and play mode. Not yet hand-tested with real
mouse/keyboard (drag-drop, gizmos, prefab workflow in the UI).

## Scripting (`Editor/Scripting`, hot reload)

Every `.cs` under `<project>/Assets/` compiles (Roslyn,
`Microsoft.CodeAnalysis.CSharp` on Editor.csproj only) into one in-memory
"Assembly-CSharp" loaded into a *collectible* `ScriptLoadContext`; its
components register in `TypeRegistry` and appear under Add Component
("Scripts/" unless `[AddComponentMenu]`). Global usings give scripts
System/Linq/Numerics/Engine.Core plus aliases `Input`, `Debug`, `Key`
(Silk), `Random` (System) — no `using` lines needed. References are limited
to the BCL, Engine and Silk.NET.Input/Maths/Core (not the editor/ImGui).

- `ScriptSystem`: FileSystemWatcher → 250 ms debounce → compile on a
  background Task → applied on the main thread at the start of
  `OnUpdate`. Startup compiles synchronously (`CompileNow`) *before* the
  scene opens so script components don't load as Missing. Compile errors
  keep the last good assembly running and set `ctx.PlayBlocker` (Play is
  refused, like Unity). Errors print as `Assets/...cs(line,col): error ...`;
  Console lines in that format (or runtime `(File.cs:42)` from
  `MonoBehaviour.SourceLocation`, which needs the emitted PDB) are
  clickable → Script Editor at that line.
- Hot swap: edit mode → `ctx.ReloadSceneInPlace()` (scene JSON round-trip;
  also resurrects MissingComponents). Play mode → per-component swap
  (`GameObject.ReplaceComponent`, same instanceId), copying *all* fields by
  name incl. private; values involving old script types (enums, script
  refs, [Serializable] classes, lists) are converted via the serializer
  after every swap is done; then a pass remaps fields pointing at old
  instances. Coroutines and static state don't survive. Deleted classes
  become MissingComponent (data kept). Afterwards `AssetDatabase.ClearCache`,
  `MonoBehaviour.ClearMessageCache(old)`, `oldContext.Unload()`.
- Each success also writes `Library/ScriptAssemblies/Assembly-CSharp.dll`
  (+pdb); `SceneRunner` loads it from bytes so standalone play has scripts.
  `<project>/Assembly-CSharp.csproj` is generated for VS Code/VS
  IntelliSense only (HintPaths to the editor's Engine.dll) — never built by
  the editor. Library/ should be git-ignored.
- `ScriptEditorPanel`: tabbed ImGui multiline editor (Consolas, tab input,
  Ctrl+S save → recompile, per-file error list with click-to-jump via
  input-text callback, picks up external edits, unsaved-close prompt). No
  syntax highlighting/autocomplete — "Open Externally" for that. New
  scripts: Inspector Add Component → "+ New Script" (type a name in the
  search box; created in Assets/Scripts, auto-added after compile),
  Project → Create → C# Script, Assets → Create C# Script. Component ⋮ →
  Edit Script. Example: `SampleProject/Assets/Scripts/Bouncer.cs`.
- Fonts load extra glyph ranges (General Punctuation, arrows, box drawing)
  so "—" etc. don't render as "?". Focus requests for the Game view and
  Script Editor are repeated until the window reports focus (first-frame
  dock layout otherwise overrides them). `--script <file>` opens a script
  at startup; layout file is now `editor_layout_v3.ini`.

Verified headlessly (scratch test driving EditorContext + ScriptSystem):
compile, live swap keeping private state + converting enums + remapping
script references, broken script keeps old code running and blocks Play,
edit-mode reload, delete → Missing → restore. Screenshot confirms Script
Editor. Not hand-tested: typing in the editor, click-to-jump, New Script
flow in the UI.
