# MyEngine FPS Mode: How It Works

This guide explains the first-person shooter mode built on top of MyEngine.
It's written to be read alongside the code. Every section names the file and
the class to open.

> **TL;DR:** the level is a **2D grid** that gets **extruded into 3D walls**.
> Almost every hard question (collision, bullets, line of sight, pathfinding,
> even "is there a wall beside me to run on?") is answered in 2D on that grid,
> the same trick Wolfenstein 3D used. Only the *drawing* is truly 3D, plus a
> separate height value for jumping.

---

## 1. Running it

```powershell
dotnet run --project Game\Game.csproj -- fps    # the FPS
dotnet run --project Game\Game.csproj           # the platformer, unchanged
```

| Input | Action |
|---|---|
| Mouse | Look around |
| W A S D / arrows | Move / strafe |
| Space | Jump. In the air, **hold W beside a wall** to wall-run. **Space again** to wall-jump |
| Left Shift | Sprint |
| Left click | Shoot (hold for the SMG) |
| 1 / 2 / 3 / 4, mouse wheel | Pistol / Shotgun / SMG / Rocket Launcher |
| R | Reload (also automatic when you click on an empty magazine) |
| Esc | Pause menu (Resume / Restart / Controls / Main menu / Quit). Esc again resumes |
| Enter | Restart after dying (Esc on the death screen goes back to the title screen) |

The game opens on a **title screen** (§11.1). Pick items with W/S, the arrow keys
or the mouse, and confirm with Enter, Space or a click.

**Goal:** survive endless **waves**. Each wave brings more enemies, and new
types show up as you go (§10). You heal 30 HP between waves.

The engine now draws text (`TextRenderer`, §11.1), but only the menus and the
death screen use it so far. During play the **wave, HP, weapon and ammo,
enemies left, kills and frames per second are still shown in the window title
bar**. The HUD shows a health bar (bottom left), ammo pips and the
selected weapon slot (bottom right).

---

## 2. File map

Everything for the FPS lives in `Engine/Fps/`, in the `Engine.Fps` namespace.
The platformer didn't need any changes.

| File | What it is | Platformer equivalent |
|---|---|---|
| `FpsWindow.cs` | Window, input, rendering, HUD | `GameWindow` |
| `FpsWorld.cs` | The game rules and state: waves, shooting, projectiles, explosions | `GameWindow.OnUpdate` |
| `FpsPlayer.cs` | Camera + body: look, walk, jump, **wall-run**, recoil, health | `Player` |
| `Weapon.cs` | `WeaponDefinition` (data), `Weapon` (ammo/reload), `Arsenal` (switching) | `Player`'s attack state |
| `Projectile.cs` | Rockets / acid globs, plus `DynamicLight` | — |
| `Enemy.cs` | Base class: health, hits, movement, sight, pathfinding | `Target` |
| `Imp.cs` `Spitter.cs` `Brute.cs` `Wisp.cs` | The four enemy types (AI state machines) | — |
| `FpsLevel.cs` | The grid map: collision, raycasts, wall detection, pathfinding, meshes | `BuildLevel()` + `Aabb` |
| `Geometry.cs` | Ray-vs-cylinder and point-vs-cylinder tests | `Aabb.Intersects` |
| `Mesh.cs` | `Mesh` (3D geometry on the GPU) + `MeshBuilder` | `Sprite`'s VAO/VBO/EBO |
| `BillboardRenderer.cs` | Draws flat sprites in 3D that always face you | `Sprite` |
| `Particles3D.cs` | Sparks, dust, blood, smoke, explosion fire | `ParticleSystem` |
| `ProceduralTextures.cs` | All the art, generated from code | the PNGs in `Assets/` |
| `TextRenderer.cs` | 5x7 pixel font baked into one atlas texture; draws strings | — |
| `FpsMenu.cs` | Title screen, pause menu, controls page (layout, selection, drawing) | — |

Shaders in `Engine/Shaders/`: `world.vert`, `billboard.vert`, `world.frag`, and
`text.vert` (paired with `sprite.frag`) for the font.

Small additions to shared engine classes:
- `Shader.SetUniform(string, Vector3)`, a new overload.
- `Texture(GL, width, height, byte[] rgba, bool repeat)`, which builds a texture
  from pixels made in code. It can also make textures that **tile** (repeat).

### Why `FpsWorld` is separate from `FpsWindow`

The first version had everything in the window class. Once there were waves,
four enemy types, projectiles and explosions, the window was doing two jobs:
running the game *and* talking to the OS and the GPU. They're now split:

```
FpsWindow  ── reads keyboard/mouse ──▶  FpsWorld.Update(dt, move, sprint, fire)
     ▲                                        │
     └──────── draws whatever state ◀─────────┘
               FpsWorld ended up in
```

`FpsWorld` has no GL or input code at all. That made it possible to test the
whole game **headlessly**, with a bot playing waves in a console app (see §15).

---

## 3. One frame, start to finish

```
FpsWindow.OnUpdate(dt)                       FpsWindow.OnRender()
  read input                                   clear colour + depth
  FpsWorld.Update:                             ┌─ 3D pass ────────────────────┐
    player.Update (walk/jump/wall-run)         │ depth test ON                │
    arsenal.Update; maybe Fire()               │ collect dynamic lights       │
    rebuild flow field if player moved cell    │ floor, ceiling, walls        │
    enemy.Update × N  (each runs Think())      │ enemies (billboards)         │
    projectiles: sweep, hit, explode           │ projectiles + particles      │
    waves: cleared? intermission? spawn?       └──────────────────────────────┘
  FOV kick from speed                          ┌─ 2D HUD pass ────────────────┐
  title bar stats                              │ same Sprite + ortho camera   │
                                               │ as the platformer            │
                                               └──────────────────────────────┘
```

---

## 4. Going from 2D to 3D: the concepts

### 4.1 Coordinates

```
        Y (up)            one map cell = 1 × 1 world unit
        │                 walls are 3 units tall
        │                 the player is 0.6 tall, eyes at 0.5 above the feet
        └──────── X  (map column →)
       ╱
      Z  (map row ↓)
```

The player and enemies store their ground position as a `Vector2` holding
(X, Z), because walls are floor-to-ceiling and so collision is 2D. Height is a
separate number: `FpsPlayer.FeetY` for the player, `Enemy.BaseY` for enemies
(Wisps fly).

### 4.2 The two matrices

```
world position ──[ view ]──▶ camera space ──[ projection ]──▶ screen
```

- **View** (`Matrix4x4.CreateLookAt(eye, eye + forward, up)`): moves and rotates
  the whole world so the camera sits at the origin looking down −Z.
  **Camera roll** (the wall-run head tilt) is done by tilting the `up` vector
  around the look direction before building this matrix.
- **Projection** (`CreatePerspectiveFieldOfView(fov, aspect, near, far)`):
  applies perspective. It's rebuilt **every frame** because the FOV widens when you go
  fast (§6.4).

### 4.3 The depth buffer

Each pixel also stores **how far away** the thing drawn there is, and the GPU only
draws a new pixel if it's closer. That needs three things: ask for one
(`PreferredDepthBufferBits = 24`), enable it for the 3D pass, and clear it every frame.
The HUD pass turns it off so the gun always draws on top.

---

## 5. The level (`FpsLevel`)

### 5.1 The map

```csharp
"################################",
"#..............#...............#",
"#..E...........#.......E.......#",   // P = player spawn, E = enemy spawn point
...                                   // # = brick wall, S = stone wall, . = floor
```

Edit `FpsLevel.DefaultMap` to change the level. All rows must be the same length.
Anything outside the map counts as wall. **Long straight walls are deliberate:
they're wall-run lanes.**

### 5.2 Grid to meshes

`BuildWallMesh` adds a quad only for wall sides that **face open floor** (hidden
faces are skipped), then batches everything into **one mesh per wall type**. The
whole level is 4 draw calls. Faces along X get shade 0.7 and faces along Z get 1.0
(the Wolfenstein trick that makes corners readable without real lighting).

Wall UVs run 0→1 across a cell and 0→`WallHeight` up it. With a repeating
texture, bricks stay the same size on a 3-unit-tall wall instead of
stretching.

### 5.3 Floor, ceiling and tiling

Each is one huge quad with UVs 0→`Width`/`Depth`, so the repeating texture
tiles once per cell. Repeating textures use **mipmaps** (`LinearMipmapLinear`)
so the distant floor doesn't shimmer, and `Nearest` up close to stay crisp.

---

## 6. The player (`FpsPlayer`)

### 6.1 Looking

Yaw (left/right) and pitch (up/down, clamped to ±83°) turn into a direction with
basic trig:

```csharp
forward = (cos(pitch)·sin(yaw),  sin(pitch),  −cos(pitch)·cos(yaw))
```

The mouse is locked (`CursorMode.Raw` = unaccelerated). Only the **change** in
position matters: `yaw += dx·sens`, `pitch −= dy·sens`. Movement uses
`FlatForward`/`FlatRight` (no pitch), so looking down doesn't walk you into the floor.

### 6.2 Walking, jumping, air control

- **Ground:** velocity ramps toward `input × MoveSpeed`, the same as the
  platformer. Input is normalized so diagonals aren't 41% faster.
- **Jump:** `Space` calls `BufferJump()`, with **coyote time** and a **jump buffer**,
  the same ideas and the same reasons as the platformer.
- **Air:** you can steer (`AirAcceleration`), but **letting go doesn't brake**,
  and steering never slows you below your current speed. That's what makes
  wall-jumps and rocket jumps carry you.
- **Landing:** a quick camera dip scaled by impact speed, eased with
  `Easing.OutCubic`, plus a dust puff on big drops.
- **Hitting a wall** zeroes the blocked part of your velocity, so stored speed
  doesn't "leak" out later.

### 6.3 Wall-running

The grid makes this simpler than in most engines. **Walls only ever face one of
four directions** (`FpsLevel.WallNormals`), so "is there a wall beside me?" is a
grid lookup just past the body's edge (`HasWallBeside`).

**Starting a wall-run** (`TryStartWallRun`) requires all of the following:
- you're airborne (a little off the floor),
- you're holding **forward** and moving faster than `WallRunMinSpeed`,
- a wall is right beside you,
- you're moving **along** it (`|dot(velocity, normal)| < 0.7`) and not straight into it,
- it isn't the wall you just jumped off (`WallReattachDelay`), which stops
  you from instantly re-grabbing it.

```
   wall ███████████████████████████
          ↑ normal points out of the wall
   you ●━━━━━━━━━━━━━━━━━▶  tangent = along the wall, in your direction of travel
```

**While running** (`UpdateWallRun`):
- Velocity is forced **along the tangent**, easing toward `WallRunSpeed`, with a
  small push into the wall to stay glued (collision stops the actual movement).
- **Gravity drops** from 18 to 4, so you sink slowly in a long arc. On latching on,
  vertical speed is **replaced** (not added to) with a small upward pop. Otherwise
  the leftover speed from your jump, under weak gravity, would launch you into the
  ceiling. A test caught exactly this happening.
- The run ends when the timer runs out (1.3 s), the wall ends, you let go of W,
  you steer away, or you touch the floor.

**Wall jump:** pressing Space while running keeps 90% of your run speed, adds a push
**out along the wall normal**, and resets vertical speed upward. Chain these
between the two walls of a corridor.

**Feel:**
- The camera rolls about 11° **away** from the wall (smoothed).
- The gun tilts with it.
- Head bob speeds up on the wall.
- The FOV widens.

**Why it's useful here:** melee enemies only hit you if your body overlaps
their height (`Enemy.PlayerInReach`), so running along a wall over an Imp or a
Brute dodges it.

### 6.4 Speed FOV kick

`FpsWindow` widens the FOV by up to 12° when you're faster than a normal
run (wall-running, rocket jumps, wall-jumps), smoothed so it never snaps. It's
cheap, and it gives a strong sense of speed.

---

## 7. Weapons (`Weapon.cs`)

### 7.1 Data-driven guns

A gun is a **`WeaponDefinition`**: plain data with magazine size, fire rate,
damage, pellets, spread, recoil, whether it's automatic, whether it fires a
projectile, and how its view model is drawn. **Adding a gun = adding one more
definition.** No new class is needed. The shared `Weapon` class runs ammo, cooldown and
reload for all of them, and `Arsenal` holds the four and animates switching (the
gun lowers, swaps at the bottom, then raises; you can't fire mid-switch).

| | Pistol | Shotgun | SMG | Rocket Launcher |
|---|---|---|---|---|
| Fire | click | click | **hold** | click |
| Magazine | 12 | 2 | 35 | 3 |
| Damage | 2 | 1 × **10 pellets** | 1 | 12 splash |
| Spread | tiny | wide (0.09 rad) | small | — |
| Special | accurate | shreds up close | fast | **projectile, splash, rocket jumps** |

**Semi-auto fire buffering:** a click that lands during the cooldown is
remembered for 0.15 s (`FpsWindow._fireBuffer`), the same idea as the jump
buffer, so fast clicking never silently eats a shot.

### 7.2 Hitscan, pellets and spread (`FpsWorld.FireHitscan`)

Each pellet is an instant ray. Find the nearest thing along it (wall, floor,
ceiling or enemy), and the closest one is what got hit. For **spread**:

```
build two axes perpendicular to forward (right, up)
pick a random point in a disk of radius tan(spread)   ← sqrt(random) keeps it even
direction = normalize(forward + right·x + up·y)
```

Shotgun pellets **add up their damage per enemy first**, then apply it once.
That gives one flash, one knockback and one hit marker per enemy instead of ten.

### 7.3 Grid raycasting with DDA (`FpsLevel.Raycast2D`)

DDA jumps from one grid line to the next, always taking whichever (vertical or horizontal) is closer.
It visits every cell the ray crosses exactly once and never skips corners:

```
 ┌───┬───┬───┬───┐
 │   │   │   │ ▓ │   ● = ray start
 ├───┼───┼──x┼───┤   x = each grid line crossed
 │   │  x│x  │   │   ▓ = first wall cell → hit (and which kind of line
 ├───┼─x─┼───┼───┤       was crossed last gives the wall's normal)
 │ ● x   │   │   │
 └───┴───┴───┴───┘
```

The 3D version runs DDA on the ray's flat floor "shadow" and also tests the
floor and ceiling planes. Distances are divided by the flat length to convert
back to 3D.

### 7.4 Projectiles (`Projectile`, `FpsWorld.UpdateProjectiles`)

Rockets and acid globs actually fly. Each frame a projectile **sweeps the
segment** from where it was to where it's going, and tests that whole segment
against walls, enemies or the player. Checking only the end point would let a
fast rocket skip through something between frames ("tunnelling"), the same
problem the platformer's per-axis collision solves.

When fired point-blank into a wall (closer than the spawn offset), the rocket
detonates right there. That's how you rocket-jump off the floor.

### 7.5 Explosions and rocket jumps (`FpsWorld.Explode`)

Everything within `SplashRadius` **that the blast can see** (2D line of sight,
so no damage through walls) takes damage that falls off linearly with distance,
and is shoved away from the centre:

```
falloff   = 1 − distance / radius
damage    = SplashDamage × falloff          (you take 35% of it: SelfSplashDamageScale)
knockback = direction_away × SplashKnockback × falloff
```

**A rocket jump is just that knockback applied to you.** An explosion under your
feet points "away" mostly upward, and `FpsPlayer.AddImpulse` adds it to your
velocity. Your air control doesn't brake (§6.2), so you keep the momentum. Every
nearby explosion also shakes the camera, harder the closer it is.

---

## 8. Enemies: one base class, four brains

### 8.1 The base class (`Enemy`)

Shared by all enemy types:
- health
- hit flash and squash (the same `Easing.OutCubic` trick as `Target`)
- knockback (divided by `KnockbackResistance`)
- death collapse and corpse
- moving with collision
- line of sight
- **flow-field pathfinding**
- separation from other enemies
- ray hit testing

Each subclass implements only **`Think(dt, world)`**, its own state machine.
This is the "base class with a virtual Update" shape the platformer roadmap
mentions for `GameObject`.

Useful helpers for `Think()`:

| Helper | Does |
|---|---|
| `ChaseDirection(world)` | Straight at you if visible, else follow the flow field around walls |
| `Move(world, dir, speed, dt)` | Moves with collision and returns how far it **actually** got (0–1). Brutes use this to notice they hit a wall |
| `PlayerInReach(world, reach)` | Horizontal reach **and** height overlap, which is why jumping or wall-running dodges melee |
| `CanSeePlayer(world)` | Range + 2D DDA line of sight |
| `WindupT` (virtual) | 0→1 during an attack telegraph. The renderer tints the enemy red by this much |

### 8.2 The four types

**Imp** (purple, 6 HP): basic melee.
```
Idle ─▶ Chase ─(in reach)─▶ Windup (0.45 s, glows red) ─▶ swipe ─▶ Recover ─▶ Chase
```
The swipe only connects if you're **still** in reach when the windup ends, so backing off or
jumping is a real defence.

**Spitter** (green slug, 5 HP): ranged. It keeps a comfortable distance (3.5–6.5 tiles),
backing off if you rush it, **strafes** sideways (flipping direction now and then or
when blocked), and lobs slow, glowing **acid globs** (10 dmg) you can see coming
and sidestep. With no line of sight it pathfinds toward you to get an angle.

**Brute** (huge, 30 HP, barely flinches): a tank with two attacks.
```
Chase ─(mid range, sees you)─▶ ChargeWindup ─▶ CHARGE ─(hits a wall)─▶ Stunned (glows blue)
  │                             (aims at you,     │
  │                              then locks in)   └─(hits you: 25 dmg + big shove / time up)─▶ Recover
  └─(close)─▶ SlamWindup ─▶ slam (20 dmg + shockwave) ─▶ Recover
```
The charge direction **locks** when the windup ends, so sidestepping makes it
miss. If it charges into a wall it **stuns itself** for 1.6 s, and that's your
opening.

**Wisp** (cyan, flying, 3 HP): fast and fragile. It hovers at head-height-plus,
approaches, pulls back slightly (the tell), then **dives** at your chest in full 3D
(8 dmg) and climbs back up after the pass. It's drawn partly **emissive** so it glows
in the dark, and when killed it **falls** out of the air (an `UpdateDead`
override).

### 8.3 Billboards and alpha cutout

Every enemy is a flat sprite that turns to face you, but only around the
vertical axis ("cylindrical"), so enemies stay upright when you look up or down.
Transparent pixels are **discarded** (`uAlphaCutoff = 0.5`) so they never write
to the depth buffer. Otherwise their empty corners would hide walls behind them.

### 8.4 Pathfinding: the flow field

A breadth-first search from the player's cell writes "steps to the player" into
every cell. Every enemy that can't see you just steps toward a lower number:

```
 # # # # # #
 # 4 3 2 3 #      One BFS serves every enemy at once, which is
 # # # 1 # #      why this beats A* per enemy for "everyone chase the
 # 2 1 P 1 #      player". It's rebuilt only when you change cell.
 # # # # # #
```

---

## 9. Lighting and fog (`world.frag`)

```glsl
attenuation = 1 / (1 + falloff · distance²)        // you carry the light
flash       = warm · muzzleFlash · e^(−0.35·distance)
dynamic     = Σ lightColor · (1 − d/radius)²        // up to 8 point lights
colour      = mix(texel · (shade·attenuation + flash + dynamic), fogColor, fog(distance))
```

**Dynamic lights** (`DynamicLight`): every projectile in flight carries one
(rockets orange, globs green), so they **light up the corridor as they fly**.
Explosions add a short bright flash. `FpsWorld.CollectLights` gathers them each
frame. The shader takes up to 8 as **uniform arrays**, set element by element by
name (`"uLightPositions[3]"`).

Fog fades to the **same colour as the clear colour**, so the distance dissolves
into darkness instead of ending at a visible edge.

### 9.1 A bug worth a devlog segment: per-vertex vs per-pixel

The first version computed "distance from the camera" in the **vertex**
shader and passed it to the fragment shader. The GPU **blends vertex outputs
linearly** across each triangle. That's exact for positions, but **wrong for
distances**: the floor is one giant quad whose corners are all 13–27 units away,
so *every* floor pixel, even the one under your feet, got a blended distance over 13
and was completely fogged out. The floor and ceiling disappeared once the map got
bigger.

**The fix:** pass the view-space **position** (`vViewPosition`, which blends
correctly) and call `length()` **per pixel** in the fragment shader. The rule of
thumb is to interpolate things that are linear (positions, UVs, colours) and compute
non-linear things (lengths, normalizations) per pixel.

---

## 10. Waves (`FpsWorld.WaveComposition`)

```
wave 1: 3 imps
wave 2: 4 imps, 1 spitter, 1 wisp
wave 3: 5 imps, 1 spitter, 2 wisps, 1 brute
...each wave: +1 imp, +1 wisp, +1 spitter every 2 waves, +1 brute every 3 (max 18 total)
```

- Enemies spawn at `E` points **more than 7 tiles from you**, and they spawn
  **already alerted**. Wave spawns are usually out of sight, and an unalerted
  enemy would idle there forever. A headless test caught exactly that: a bot sat
  on wave 1 for 4 minutes with 0 kills.
- When a wave is cleared: heal 30 HP, a 3-second intermission (with a countdown in the title bar), then
  the next wave arrives with a screen pulse.
- Change the difficulty curve by editing `WaveComposition`, which is just a few lines.

---

## 11. The HUD and view model

The same `Sprite` + orthographic pixel projection as the platformer. Each
weapon's view model is its own procedural texture. Its position adds up:

| Motion | Idea |
|---|---|
| Walk sway | figure-8: `x = sin(bob/2)`, `y = −|cos(bob/2)|` × speed |
| Air lag | rises while falling, dips while rising |
| Wall-run | shifts and rotates with the camera roll |
| Shot kick | pushed down by `OutCubic(KickT)` |
| Reload / switch | dips out of view and back |

The crosshair gap scales with the weapon's spread, opens on each shot, and turns
red on a hit. Damage gives a red screen flash plus shake.

### 11.1 Text and menus (`TextRenderer`, `FpsMenu`)

**Text.** Each letter is drawn in code as 7 strings of `#` and `.`. At startup
`TextRenderer` paints every letter into one `Pixmap`, side by side. This is the
**font atlas**: one texture holding all the letters. It also remembers each
letter's rectangle in UV units. `text.vert` is `sprite.vert` with one extra
uniform, `uUvRect`, which remaps the quad's 0..1 UVs to just that rectangle. So
drawing `"PLAY"` means drawing four quads that each show a different part of the
same texture. Text is scaled by whole numbers and snapped to whole pixels, so
each font pixel covers an exact block of screen pixels and stays sharp. Every
string gets a drop shadow: a dark copy one font pixel down and to the right.

**Menus.** `FpsMenu` follows the same split as `FpsWorld`: it never touches
Silk.NET input. `FpsWindow` turns keys and the mouse into `MoveSelection`,
`Confirm`, `Back`, `Hover` and `Click`, and gets back a `MenuCommand` (`Play`,
`Resume`, `Restart`, `MainMenu`, `Quit`). Moving between pages (Controls, Back)
happens inside the menu. Each item's rectangle comes from one function,
`ItemRect`, which is used both to draw the highlight and to check whether the
mouse is over an item, so the two can't disagree. The mouse is converted from
Silk's top-left, Y-down coordinates to the HUD's bottom-left, Y-up coordinates
once, in `MouseInHudSpace`.

While any menu is open, `FpsWorld.Update` isn't called, so pausing really
freezes the game. The title screen shows the empty arena from the spawn point
with a slowly turning camera (`DrawWorld` takes the camera as parameters now).
Losing window focus mid-fight opens the pause menu. Sizes are based on a whole
number `unit = windowHeight / 360` (2 at 720p, 3 at 1080p). "Best this session"
is kept in memory only and isn't saved to disk. The title text is
`FpsMenu.GameTitle`.

---

## 12. Art from code (`ProceduralTextures`)

There are no image files for this mode (except the reused `particle.png`). Each
texture is a function that paints into a `Pixmap` (RGBA bytes, row 0 at the bottom,
like GL) using `FillRect`, `FillEllipse` and per-pixel loops. Variation comes from
`Hash(x, y, seed)`, a deterministic pseudo-random function, so the art is the same
every run. To use real art instead, swap a call for
`new Texture(gl, path)`. Nothing else changes.

---

## 13. Tuning knobs

All public fields.

| Class | Field | Default | Effect |
|---|---|---|---|
| `FpsPlayer` | `MoveSpeed` / `SprintMultiplier` | 3.6 / 1.5 | Tiles per second |
| | `JumpVelocity` / `Gravity` | 6.2 / 18 | Jump height ≈ v²/2g ≈ 1.07 |
| | `AirAcceleration` | 9 | Air steering strength |
| | `WallRunMinSpeed` / `WallRunSpeed` | 2.5 / 6.5 | Speed needed to latch on / speed you're pulled to |
| | `WallRunDuration` / `WallRunGravity` | 1.3 s / 4 | How long and how floaty |
| | `WallJumpPush` / `WallJumpUp` | 5 / 5.8 | Wall-jump direction mix |
| | `WallRunCameraRoll` | 0.2 rad | Head tilt |
| `WeaponDefinition` | everything | see §7 | Each gun is one block of data |
| `Enemy` subclasses | health, speeds, ranges, windups, damage | per type | Each AI's timings |
| `FpsWorld` | `WaveComposition`, `HealBetweenWaves`, `SelfSplashDamageScale`, `MaxEnemiesPerWave` | | Difficulty |
| `FpsWindow` | `FogStart`/`FogEnd`/`LightFalloff`, FOV, `MaxSpeedFovKickDegrees` | | Mood |

---

## 14. Known limitations / next steps

- **The in-game HUD has no text yet**, so stats are in the title bar. `TextRenderer`
  is ready for it (wave banner, ammo count, kills).
- **Walls are all one height**: no ledges, stairs or platforms to reach. Wall-running
  is about speed, dodging and getting over enemies, not reaching places. A
  height-per-cell map would be the next step.
- **No ammo pickups**: magazines refill on reload, with infinite reserve ammo.
- **No sound** (Ep6).
- **Particles aren't depth-sorted** with each other. That's fine for small soft dots.
- **The FPS has its own lighting** and doesn't use the platformer's light-map/bloom
  pipeline.
- **Not playtested for feel by a human yet.** See below for what *was* checked.

## 15. How it was tested

Because `FpsWorld` has no GL or input code, a scratch console app (outside the
repo) could drive it directly:

| Check | Result |
|---|---|
| Wall-run along a 9-tile wall | 1.28 s on the wall, the full length, 1.68 s airtime vs 0.67 s for a plain jump |
| Wall-jump | Fires and pushes you away from the wall |
| Rocket jump at your feet | About 1.6 units high (plain jump about 1.07) for 4 self-damage |
| Shotgun at 1.2 tiles | Kills a 6 HP Imp in one shot |
| Each enemy vs a stationary player | All four land hits: Imp 12, Spitter 10, Brute 25 charge / 20 slam, Wisp 8 |
| Aim-bot playing 4 minutes | Reached wave 11, 141 kills, all four enemy types, Brutes stunned themselves on walls 7 times, no crashes |

Plus a launch-and-screenshot check of the real window: shaders compile, all the art
renders, and it runs at 60 fps (vsync).
