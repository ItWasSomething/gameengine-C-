# MyEngine

A from-scratch game engine written in **C# / .NET 10**, rendering with **OpenGL via
[Silk.NET](https://github.com/dotnet/Silk.NET)**, built as a YouTube devlog series.

It started as a small 2D engine and grew into four runnable things:

| Mode | What it is |
|------|------------|
| **2D platformer** | Character controller with acceleration, coyote time, jump buffering, jump-cut, squash & stretch, particles, melee attacks on practice dummies, and FBO-based 2D lighting. |
| **FPS mode** | A Wolfenstein-style wave shooter: grid levels extruded to 3D, wall-running, rocket jumps, four weapons, four enemy types with flow-field pathfinding, in-game menus. |
| **3D editor** | A Unity-style editor (ImGui docking) with GameObjects + Components, Unity-named APIs, scene/prefab serialization, undo, Play mode, **C# hot-reload scripting** via Roslyn, a project hub, one-click **Build Game** (standalone Windows .zip) and **Publish**. |
| **MyEngine Games** (`Website/`) | A full-stack site to share games: ASP.NET Core + SQLite API, React + TypeScript front end, GitHub sign-in, screenshots, versions, downloads, likes, comments, tags and profiles. The editor publishes to it directly. See [`Website/README.md`](Website/README.md). |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A GPU/driver with OpenGL 3.3+
- [Node.js](https://nodejs.org) 20+ (only for the website front end)
- Developed and tested on Windows

## Getting started

```powershell
git clone https://github.com/ItWasSomething/gameengine-C-.git
cd gameengine-C-
dotnet build MyEngine.slnx
```

Then run one of the modes:

```powershell
# 2D platformer
dotnet run --project Game/Game.csproj

# FPS mode
dotnet run --project Game/Game.csproj -- fps

# 3D editor (opens the project hub; or pass a project folder)
dotnet run --project Editor/Editor.csproj
dotnet run --project Editor/Editor.csproj -- SampleProject

# Website (two terminals; see Website/README.md)
dotnet run --project Website/Server
cd Website/Client; npm install; npm run dev     # → http://localhost:5173

# Play a saved scene standalone
dotnet run --project Game/Game.csproj -- scene SampleProject/Assets/Scenes/Main.scene
```

### Controls

**Platformer:** A/D or ←/→ move · Space/W/↑ jump · J/X/Enter attack

**FPS:** WASD move · Shift sprint · mouse look · Space jump (run along walls to wall-run) ·
LMB fire · 1–4 / mouse wheel switch weapon · R reload · Esc pause

**Editor:** RMB + WASD fly · Alt + LMB orbit · MMB pan · F frame selection ·
W/E/R move/rotate/scale gizmo · Ctrl+P play · Ctrl+Shift+P pause ·
Ctrl+B build · **F1 lists every shortcut**

## Share a game

1. In the editor: **File → Build Settings…** — set the name and version.
2. **File → Publish to MyEngine Games…** — sign in (your browser opens; approve
   there), fill in the tagline/description/tags, capture a few screenshots
   from the Game view, and press **Build & Publish**.
3. Later updates: bump the version and publish again — it uploads a new
   version of the same game.

**Build Game** alone (Ctrl+B) writes `Builds/<Name>-<version>-win64.zip`
into the project: unzip and run the `.exe`, no .NET install needed. Building
needs the MyEngine source folder and the .NET SDK, since it compiles the player.

## Project layout

```
MyEngine.slnx
├─ Engine/          reusable engine library (rendering, 2D game, FPS mode, Core GameObject system)
│  ├─ Core/         GameObject / Component / Transform / Scene / serialization (Unity-style API)
│  ├─ Rendering/    3D renderer (PBR, lights, sky)
│  ├─ Fps/          FPS mode
│  ├─ Shaders/      GLSL shaders (copied to the output folder)
│  └─ Assets/       textures
├─ Game/            console entry point (platformer / fps / standalone scene player)
├─ Editor/          ImGui editor + Roslyn hot-reload scripting + Build/Publish
├─ SampleProject/   example project the editor opens (scenes + scripts)
├─ Website/
│  ├─ Server/       ASP.NET Core API + SQLite (games, users, uploads)
│  └─ Client/       React + TypeScript site
└─ docs/            guides (FPS-Guide.md, code guide PDF)
```

## Writing scripts in the editor

Any `.cs` file under `SampleProject/Assets/` is compiled live into the editor and
hot-reloaded on save — no restart needed, even during Play mode:

```csharp
public class Hover : MonoBehaviour
{
    [Range(0.1f, 5f)] public float height = 1f;

    private Vector3 _start;

    void Start() => _start = transform.localPosition;

    void Update()
    {
        float y = MathF.Sin(Time.time * 2f) * height;
        transform.localPosition = _start + new Vector3(0, y, 0);
    }
}
```

See `SampleProject/Assets/Scripts/` for examples.

## Status

This is a learning project and a work in progress — see the devlog roadmap in
[`CLAUDE.md`](CLAUDE.md) for what is done and what's next.

## License

[MIT](LICENSE)
