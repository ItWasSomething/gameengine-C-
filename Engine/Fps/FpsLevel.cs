using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Fps;

// The result of shooting a ray into the level.
public readonly record struct RayHit(bool Hit, float Distance, Vector3 Point, Vector3 Normal);

// A grid-based level, Wolfenstein style: every cell is either solid wall or
// open floor, and walls are always exactly one cell wide and WallHeight tall.
//
// World axes: X = map column, Z = map row, Y = up. One map cell = 1x1 world
// units. Because the level is really a 2D grid extruded upward, almost every
// question about it (collision, line of sight, bullets, pathfinding) can be
// answered in 2D on the X/Z plane — the same trick the original raycasters used.
public sealed class FpsLevel
{
    // Tall enough to jump around in and wall-run along (the player is 0.6 tall).
    public const float WallHeight = 3f;

    // '#' brick wall, 'S' stone wall, '.' floor,
    // 'P' player spawn, 'E' enemy spawn point (waves pick from these).
    // Long straight walls are deliberate: they're wall-run lanes.
    public static readonly string[] DefaultMap =
    {
        "################################",
        "#..............#...............#",
        "#..E...........#.......E.......#",
        "#..............#...............#",
        "#....######....#....######.....#",
        "#..............................#",
        "#.........E..........E.........#",
        "#######....#########....########",
        "#..............#...............#",
        "#..E...........#.......E.......#",
        "#..............S...............#",
        "#......P.......S...............#",
        "#..............S...............#",
        "#######....#########....########",
        "#..............................#",
        "#.........E..........E.........#",
        "#....SSSSSS..........SSSSSS....#",
        "#..............#...............#",
        "#..E...........#.......E.......#",
        "#..............#...............#",
        "################################",
    };


    private readonly char[,] _tiles; // [x, z]

    public int Width { get; }
    public int Depth { get; }
    public Vector2 PlayerSpawn { get; private set; }
    public IReadOnlyList<Vector2> EnemySpawns => _enemySpawns;
    private readonly List<Vector2> _enemySpawns = new();

    public FpsLevel(string[] rows)
    {
        Depth = rows.Length;
        Width = rows[0].Length;
        if (rows.Any(r => r.Length != Width))
            throw new ArgumentException("Every map row must be the same length.");

        _tiles = new char[Width, Depth];
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            char c = rows[z][x];
            // Spawn markers are just floor once their position is recorded.
            // +0.5 = the middle of the cell rather than its corner.
            if (c == 'P') { PlayerSpawn = new Vector2(x + 0.5f, z + 0.5f); c = '.'; }
            if (c == 'E') { _enemySpawns.Add(new Vector2(x + 0.5f, z + 0.5f)); c = '.'; }
            _tiles[x, z] = c;
        }
    }

    // Anything outside the map counts as wall, so nothing can ever leave it.
    public bool IsWall(int x, int z) =>
        x < 0 || z < 0 || x >= Width || z >= Depth || _tiles[x, z] != '.';

    public static (int X, int Z) CellOf(Vector2 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    // ---------------------------------------------------------------- collision

    // Moves a circle-ish body (treated as a square of half-size `radius`)
    // by `delta`, one axis at a time — exactly the platformer's X-pass-then-
    // Y-pass approach, here X then Z. Resolving axes separately is what makes
    // you SLIDE along a wall when you walk into it at an angle instead of
    // sticking to it.
    public Vector2 MoveAndCollide(Vector2 position, Vector2 delta, float radius)
    {
        const float skin = 0.001f; // stop a hair short so we're never "inside" next frame

        position.X += delta.X;
        if (delta.X != 0f && OverlapsWall(position, radius, out int wallX, out _, axisX: true))
            position.X = delta.X > 0f ? wallX - radius - skin : wallX + 1 + radius + skin;

        position.Y += delta.Y;
        if (delta.Y != 0f && OverlapsWall(position, radius, out _, out int wallZ, axisX: false))
            position.Y = delta.Y > 0f ? wallZ - radius - skin : wallZ + 1 + radius + skin;

        return position;
    }

    // Checks every cell the body's square footprint touches. Reports the
    // nearest blocking cell along the axis being resolved.
    private bool OverlapsWall(Vector2 p, float r, out int hitX, out int hitZ, bool axisX)
    {
        int minX = (int)MathF.Floor(p.X - r), maxX = (int)MathF.Floor(p.X + r);
        int minZ = (int)MathF.Floor(p.Y - r), maxZ = (int)MathF.Floor(p.Y + r);
        hitX = hitZ = 0;
        bool found = false;

        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            if (!IsWall(x, z)) continue;
            // Keep the cell closest to the body on the axis we're resolving.
            if (!found
                || (axisX && MathF.Abs(x + 0.5f - p.X) < MathF.Abs(hitX + 0.5f - p.X))
                || (!axisX && MathF.Abs(z + 0.5f - p.Y) < MathF.Abs(hitZ + 0.5f - p.Y)))
            {
                hitX = x; hitZ = z;
            }
            found = true;
        }
        return found;
    }

    // ---------------------------------------------------------------- raycasting

    // 2D grid raycast using DDA ("digital differential analyzer"): instead of
    // marching the ray forward in tiny fixed steps (slow, and can skip thin
    // corners), jump straight from one grid line to the next, always taking
    // whichever of the next vertical/horizontal grid line is closer. Every
    // cell the ray passes through gets visited exactly once.
    // `direction` must be normalized. Returns distance along the ray.
    public bool Raycast2D(Vector2 origin, Vector2 direction, float maxDistance, out float distance, out Vector2 normal)
    {
        int cellX = (int)MathF.Floor(origin.X);
        int cellZ = (int)MathF.Floor(origin.Y);

        // How far along the ray we must travel to cross one whole cell in X / in Z.
        float deltaX = direction.X == 0f ? float.MaxValue : MathF.Abs(1f / direction.X);
        float deltaZ = direction.Y == 0f ? float.MaxValue : MathF.Abs(1f / direction.Y);

        int stepX = direction.X < 0f ? -1 : 1;
        int stepZ = direction.Y < 0f ? -1 : 1;

        // Distance along the ray to the FIRST vertical / horizontal grid line.
        float nextX = direction.X < 0f ? (origin.X - cellX) * deltaX : (cellX + 1 - origin.X) * deltaX;
        float nextZ = direction.Y < 0f ? (origin.Y - cellZ) * deltaZ : (cellZ + 1 - origin.Y) * deltaZ;

        distance = 0f;
        normal = Vector2.Zero;

        while (distance <= maxDistance)
        {
            if (nextX < nextZ)
            {
                distance = nextX;
                nextX += deltaX;
                cellX += stepX;
                normal = new Vector2(-stepX, 0f); // we entered through a vertical (X) face
            }
            else
            {
                distance = nextZ;
                nextZ += deltaZ;
                cellZ += stepZ;
                normal = new Vector2(0f, -stepZ);
            }

            if (IsWall(cellX, cellZ))
                return distance <= maxDistance;
        }
        return false;
    }

    // Full 3D bullet ray: the walls via the 2D DDA above (walls are full
    // height, so if the ray's shadow on the floor reaches a wall, the ray hits
    // it — unless it hit the floor or ceiling first, which is a plane test).
    public RayHit Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        float best = maxDistance;
        Vector3 bestNormal = Vector3.Zero;
        bool hit = false;

        // Floor (y = 0) and ceiling (y = WallHeight).
        if (direction.Y < 0f)
        {
            float t = -origin.Y / direction.Y;
            if (t < best) { best = t; bestNormal = Vector3.UnitY; hit = true; }
        }
        else if (direction.Y > 0f)
        {
            float t = (WallHeight - origin.Y) / direction.Y;
            if (t < best) { best = t; bestNormal = -Vector3.UnitY; hit = true; }
        }

        // The 2D DDA measures distance along the ray's FLAT (X/Z) shadow; a ray
        // pointing partly up/down covers less ground per unit of 3D length, so
        // divide by the flat length to convert back to 3D distance.
        var flat = new Vector2(direction.X, direction.Z);
        float flatLength = flat.Length();
        if (flatLength > 1e-5f &&
            Raycast2D(new Vector2(origin.X, origin.Z), flat / flatLength, best * flatLength, out float d2, out Vector2 n2))
        {
            float t = d2 / flatLength;
            if (t < best) { best = t; bestNormal = new Vector3(n2.X, 0f, n2.Y); hit = true; }
        }

        return new RayHit(hit, best, origin + direction * best, bestNormal);
    }

    public bool HasLineOfSight(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length < 1e-4f) return true;
        return !Raycast2D(from, delta / length, length, out _, out _);
    }

    // ---------------------------------------------------------------- wall-running

    // Grid walls only ever face one of these four ways, which is what keeps
    // wall-run detection simple: no arbitrary angles, just "is there a wall
    // cell right beside me on this side?".
    public static readonly Vector2[] WallNormals =
    {
        new(1f, 0f), new(-1f, 0f), new(0f, 1f), new(0f, -1f),
    };

    // Is there a wall touching the body on the side that `normal` points away
    // from? (A wall whose normal is +X sits on our -X side.) Probes a little
    // past the body's edge, at the body's centre row/column.
    public bool HasWallBeside(Vector2 position, float radius, Vector2 normal, float probe = 0.08f)
    {
        Vector2 point = position - normal * (radius + probe);
        var (x, z) = CellOf(point);
        return IsWall(x, z);
    }

    // ---------------------------------------------------------------- pathfinding

    // A "flow field": breadth-first search outward from the target cell,
    // recording how many steps every reachable cell is from it. An enemy
    // anywhere on the map then just steps into whichever neighbouring cell has
    // a smaller number — and it'll walk around walls to reach the target.
    // One BFS serves every enemy at once, which is why this beats running A*
    // per enemy for "everyone chase the player".
    public int[,] BuildFlowField(Vector2 target)
    {
        var field = new int[Width, Depth];
        for (int x = 0; x < Width; x++)
        for (int z = 0; z < Depth; z++)
            field[x, z] = int.MaxValue;

        var (tx, tz) = CellOf(target);
        if (IsWall(tx, tz)) return field;

        var queue = new Queue<(int X, int Z)>();
        field[tx, tz] = 0;
        queue.Enqueue((tx, tz));

        while (queue.Count > 0)
        {
            var (x, z) = queue.Dequeue();
            int next = field[x, z] + 1;
            foreach (var (nx, nz) in Neighbours(x, z))
            {
                if (IsWall(nx, nz) || field[nx, nz] <= next) continue;
                field[nx, nz] = next;
                queue.Enqueue((nx, nz));
            }
        }
        return field;
    }

    public static IEnumerable<(int X, int Z)> Neighbours(int x, int z)
    {
        yield return (x + 1, z);
        yield return (x - 1, z);
        yield return (x, z + 1);
        yield return (x, z - 1);
    }

    // ---------------------------------------------------------------- meshes

    // One mesh per wall type (so each can use its own texture). Only faces
    // that border open floor are emitted — the wall between two solid cells
    // can never be seen, so there's no point sending it to the GPU.
    public Mesh? BuildWallMesh(GL gl, char wallType)
    {
        var builder = new MeshBuilder();
        const float h = WallHeight;
        // Faces pointing along X are darker than faces pointing along Z, so
        // two walls meeting at a corner never blend into one flat colour.
        const float shadeX = 0.7f, shadeZ = 1f;
        // V runs 0..WallHeight (not 0..1) so the repeat-wrapped brick texture
        // tiles up the wall at the same scale as across it instead of stretching.
        var uvMin = Vector2.Zero;
        var uvMax = new Vector2(1f, WallHeight);

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            if (_tiles[x, z] != wallType) continue;

            if (!IsWall(x - 1, z)) // west face, seen from -X
                builder.AddQuad(new(x, 0, z), new(x, 0, z + 1), new(x, h, z + 1), new(x, h, z), uvMin, uvMax, shadeX);
            if (!IsWall(x + 1, z)) // east face, seen from +X
                builder.AddQuad(new(x + 1, 0, z + 1), new(x + 1, 0, z), new(x + 1, h, z), new(x + 1, h, z + 1), uvMin, uvMax, shadeX);
            if (!IsWall(x, z - 1)) // north face, seen from -Z
                builder.AddQuad(new(x + 1, 0, z), new(x, 0, z), new(x, h, z), new(x + 1, h, z), uvMin, uvMax, shadeZ);
            if (!IsWall(x, z + 1)) // south face, seen from +Z
                builder.AddQuad(new(x, 0, z + 1), new(x + 1, 0, z + 1), new(x + 1, h, z + 1), new(x, h, z + 1), uvMin, uvMax, shadeZ);
        }

        return builder.IsEmpty ? null : builder.Build(gl);
    }

    // A single big quad covering the whole map at height y. Its UVs run from
    // 0 to Width/Depth (not 0..1), so with a REPEAT-wrapped texture the image
    // tiles once per cell.
    public Mesh BuildFlatMesh(GL gl, float y, float shade)
    {
        var builder = new MeshBuilder();
        builder.AddQuad(new(0, y, 0), new(Width, y, 0), new(Width, y, Depth), new(0, y, Depth),
            Vector2.Zero, new Vector2(Width, Depth), shade);
        return builder.Build(gl);
    }
}
