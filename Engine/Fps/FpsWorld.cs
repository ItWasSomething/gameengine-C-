using System.Numerics;

namespace Engine.Fps;

// The FPS game itself: level, player, weapons, enemies, projectiles,
// particles, waves — and the rules connecting them (what a bullet hits, what
// an explosion does). No rendering and no input devices: FpsWindow reads
// input, passes it in, and draws whatever state this ends up in.
//
// Enemies get a reference to this so they can ask about the world ("where's
// the player?", "is the way clear?") and act on it (HurtPlayer, SpawnProjectile).
public sealed class FpsWorld
{
    public enum Phase { Intermission, Fighting, Dead }

    public FpsLevel Level { get; }
    public FpsPlayer Player { get; private set; } = null!;
    public Arsenal Arsenal { get; private set; } = null!;
    public List<Enemy> Enemies { get; } = new();
    public List<Projectile> Projectiles { get; } = new();
    public Particles3D Particles { get; } = new(capacity: 1024);
    public int[,] FlowField { get; private set; } = null!;

    public Phase CurrentPhase { get; private set; }
    public int Wave { get; private set; }
    public int Kills { get; private set; }
    public float IntermissionTimer { get; private set; }
    public int EnemiesAlive => Enemies.Count(e => e.IsAlive);

    public float IntermissionDuration = 3f;
    public int HealBetweenWaves = 30;
    public float SelfSplashDamageScale = 0.35f; // your own rockets hurt, but less
    public int MaxEnemiesPerWave = 18;

    // Feedback hooks for the window (screen flashes, hit marker, ...).
    public event Action<int>? PlayerDamaged;
    public event Action? EnemyHit;
    public event Action<int>? WaveStarted;
    public event Action<WeaponDefinition>? WeaponFired;

    private static readonly Vector3 SparkColor = new(1f, 0.85f, 0.45f);
    private static readonly Vector3 DustColor = new(0.55f, 0.5f, 0.45f);
    private static readonly Vector3 FireColor = new(1f, 0.55f, 0.15f);
    private static readonly Vector3 SmokeColor = new(0.35f, 0.33f, 0.32f);

    private readonly List<DynamicLight> _flashLights = new();
    private (int X, int Z) _flowFieldCell = (-1, -1);
    private readonly Random _random = new();

    public FpsWorld(FpsLevel level)
    {
        Level = level;
        Reset();
    }

    public void Reset()
    {
        Player = new FpsPlayer
        {
            Position = Level.PlayerSpawn,
            Yaw = MathF.PI / 2f, // face +X (east)
        };
        Player.Damaged += amount => PlayerDamaged?.Invoke(amount);
        Player.Died += () => CurrentPhase = Phase.Dead;
        Player.Landed += impact =>
        {
            if (impact > 5f) // only real drops kick up dust, not every little hop
                Particles.Emit(FeetPosition(), Vector3.UnitY, count: 8, DustColor,
                    spread: 1.4f, minSpeed: 0.4f, maxSpeed: 1.2f, life: 0.5f, size: 0.07f, gravity: 1f);
        };
        Player.WallJumped += () => Particles.Emit(FeetPosition() + new Vector3(0f, 0.3f, 0f), Vector3.UnitY, count: 6, DustColor,
            spread: 1.2f, minSpeed: 0.3f, maxSpeed: 1f, life: 0.4f, size: 0.06f, gravity: 1f);

        Arsenal = new Arsenal();
        Enemies.Clear();
        Projectiles.Clear();
        Particles.Clear();
        _flashLights.Clear();
        _flowFieldCell = (-1, -1);
        RebuildFlowFieldIfNeeded();

        Wave = 0;
        Kills = 0;
        CurrentPhase = Phase.Intermission;
        IntermissionTimer = 1.5f;
    }

    private Vector3 FeetPosition() => new(Player.Position.X, Player.FeetY + 0.02f, Player.Position.Y);

    // ===================================================================== update

    public void Update(float dt, Vector2 moveInput, bool sprint, bool fireRequested)
    {
        Particles.Update(dt);
        UpdateFlashLights(dt);

        if (CurrentPhase == Phase.Dead)
        {
            // The world keeps going a little (corpses settle, rockets land),
            // but the player is out.
            foreach (var enemy in Enemies) enemy.Update(dt, this);
            UpdateProjectiles(dt);
            return;
        }

        Player.Update(dt, moveInput, sprint, Level);
        Arsenal.Update(dt);
        if (fireRequested && Arsenal.TryFire())
            Fire(Arsenal.Current.Definition);

        RebuildFlowFieldIfNeeded();
        // ToArray: an enemy's Update can't add enemies today, but iterating a
        // snapshot keeps that from ever becoming a "collection modified" crash.
        foreach (var enemy in Enemies.ToArray()) enemy.Update(dt, this);
        UpdateProjectiles(dt);
        UpdateWaves(dt);
    }

    // Rebuild the pathfinding field only when the player changes cell — it's
    // a BFS over the whole map: cheap, but no need to do it every frame.
    private void RebuildFlowFieldIfNeeded()
    {
        var cell = FpsLevel.CellOf(Player.Position);
        if (cell == _flowFieldCell) return;
        FlowField = Level.BuildFlowField(Player.Position);
        _flowFieldCell = cell;
    }

    // ===================================================================== waves

    private void UpdateWaves(float dt)
    {
        if (CurrentPhase == Phase.Fighting && EnemiesAlive == 0)
        {
            CurrentPhase = Phase.Intermission;
            IntermissionTimer = IntermissionDuration;
            Player.Heal(HealBetweenWaves);
        }

        if (CurrentPhase == Phase.Intermission)
        {
            IntermissionTimer -= dt;
            if (IntermissionTimer <= 0f) StartWave(Wave + 1);
        }
    }

    // Each wave mixes in more and tougher enemies. Tweak these formulas to
    // change the difficulty curve:
    //   wave 1: 3 imps
    //   wave 2: 4 imps, 1 spitter, 1 wisp
    //   wave 3: 5 imps, 1 spitter, 2 wisps, 1 brute ... and so on.
    public static List<EnemyKind> WaveComposition(int wave)
    {
        var kinds = new List<EnemyKind>();
        kinds.AddRange(Enumerable.Repeat(EnemyKind.Imp, 2 + wave));
        if (wave >= 2) kinds.AddRange(Enumerable.Repeat(EnemyKind.Spitter, 1 + (wave - 2) / 2));
        if (wave >= 2) kinds.AddRange(Enumerable.Repeat(EnemyKind.Wisp, wave - 1));
        if (wave >= 3) kinds.AddRange(Enumerable.Repeat(EnemyKind.Brute, 1 + (wave - 3) / 3));
        return kinds;
    }

    private void StartWave(int wave)
    {
        Wave = wave;
        CurrentPhase = Phase.Fighting;
        Enemies.RemoveAll(e => !e.IsAlive); // clear out last wave's corpses

        // Spawn away from the player so nothing pops in right in your face.
        var spawns = Level.EnemySpawns.Where(s => Vector2.Distance(s, Player.Position) > 7f).ToList();
        if (spawns.Count == 0) spawns = Level.EnemySpawns.ToList();

        // Shuffle the list so that when it's capped, the cut isn't always the same kind.
        var kinds = WaveComposition(wave).OrderBy(_ => _random.Next()).Take(MaxEnemiesPerWave);
        foreach (var kind in kinds)
        {
            Vector2 spawn = spawns[_random.Next(spawns.Count)]
                + new Vector2(Rand(-0.3f, 0.3f), Rand(-0.3f, 0.3f));
            Enemy enemy = kind switch
            {
                EnemyKind.Imp => new Imp(spawn),
                EnemyKind.Spitter => new Spitter(spawn),
                EnemyKind.Brute => new Brute(spawn),
                _ => new Wisp(spawn),
            };
            enemy.Alert();
            enemy.Died += OnEnemyDied;
            Enemies.Add(enemy);
        }

        WaveStarted?.Invoke(wave);
    }

    private void OnEnemyDied(Enemy enemy)
    {
        Kills++;
        int count = enemy.Kind == EnemyKind.Brute ? 50 : 24;
        Particles.Emit(enemy.Center, Vector3.UnitY, count, enemy.BloodColor,
            spread: 1.2f, minSpeed: 1f, maxSpeed: 3f, life: 0.6f, size: 0.08f);
    }

    // ===================================================================== shooting

    private void Fire(WeaponDefinition weapon)
    {
        Vector3 origin = Player.EyePosition;
        Vector3 forward = Player.ViewForward; // includes recoil/shake: matches the crosshair

        if (weapon.ProjectileSpeed > 0f)
            FireRocket(weapon, origin, forward);
        else
            FireHitscan(weapon, origin, forward);

        Player.AddRecoil(weapon.Recoil);
        WeaponFired?.Invoke(weapon);
        if (weapon.Shake > 0f) Player.Shake(weapon.Shake, 0.12f);
    }

    // Hitscan: each pellet is an instant ray. Find the nearest thing along it
    // (wall/floor/ceiling or an enemy); whichever is closest got hit. Damage
    // from all pellets is added up per enemy first, so a shotgun blast flashes
    // and knocks back each enemy once, not ten times.
    private void FireHitscan(WeaponDefinition weapon, Vector3 origin, Vector3 forward)
    {
        var damageByEnemy = new Dictionary<Enemy, int>();
        Vector2 flatForward = new Vector2(forward.X, forward.Z);
        flatForward = flatForward.LengthSquared() > 1e-6f ? Vector2.Normalize(flatForward) : Vector2.Zero;

        for (int i = 0; i < weapon.Pellets; i++)
        {
            Vector3 direction = SpreadDirection(forward, weapon.Spread);

            RayHit worldHit = Level.Raycast(origin, direction, weapon.Range);
            float closest = worldHit.Hit ? worldHit.Distance : weapon.Range;

            Enemy? hitEnemy = null;
            foreach (var enemy in Enemies)
            {
                if (enemy.RayIntersect(origin, direction, out float distance) && distance < closest)
                {
                    closest = distance;
                    hitEnemy = enemy;
                }
            }

            Vector3 hitPoint = origin + direction * closest;
            if (hitEnemy != null)
            {
                damageByEnemy[hitEnemy] = damageByEnemy.GetValueOrDefault(hitEnemy) + weapon.Damage;
                Particles.Emit(hitPoint, direction, count: 5, hitEnemy.BloodColor,
                    spread: 0.7f, minSpeed: 1f, maxSpeed: 3.5f, life: 0.45f, size: 0.06f);
            }
            else if (worldHit.Hit)
            {
                // Sparks bounce off along the surface normal, plus a slower
                // hanging puff of dust. Nudged off the surface so they don't
                // start inside it.
                Vector3 spawn = hitPoint + worldHit.Normal * 0.02f;
                Particles.Emit(spawn, worldHit.Normal, count: weapon.Pellets > 1 ? 2 : 6, SparkColor,
                    spread: 0.9f, minSpeed: 2f, maxSpeed: 5f, life: 0.25f, size: 0.04f);
                Particles.Emit(spawn, worldHit.Normal, count: weapon.Pellets > 1 ? 1 : 4, DustColor,
                    spread: 0.5f, minSpeed: 0.3f, maxSpeed: 0.8f, life: 0.6f, size: 0.09f, gravity: 0.5f);
            }
        }

        foreach (var (enemy, damage) in damageByEnemy)
            enemy.TakeDamage(damage, flatForward, weapon.Knockback * damage);
        if (damageByEnemy.Count > 0) EnemyHit?.Invoke();
    }

    // A random direction inside a cone around `forward`. Builds two
    // perpendicular "sideways" axes, picks a random point in a disk on that
    // plane, and leans the forward vector toward it.
    private Vector3 SpreadDirection(Vector3 forward, float spread)
    {
        if (spread <= 0f) return forward;

        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, forward);

        // sqrt() spreads points evenly over the disk instead of bunching them in the middle.
        float angle = Rand(0f, MathF.Tau);
        float radius = MathF.Sqrt((float)_random.NextDouble()) * MathF.Tan(spread);
        return Vector3.Normalize(forward + right * (MathF.Cos(angle) * radius) + up * (MathF.Sin(angle) * radius));
    }

    private void FireRocket(WeaponDefinition weapon, Vector3 origin, Vector3 forward)
    {
        // Spawn it a little in front of the eye so it doesn't fill the whole
        // screen on frame one — unless a wall is closer than that, in which
        // case it detonates right there (point-blank rocket = rocket jump).
        const float spawnDistance = 0.3f;
        RayHit nearHit = Level.Raycast(origin, forward, spawnDistance);
        if (nearHit.Hit)
        {
            Explode(nearHit.Point + nearHit.Normal * 0.05f, weapon.SplashRadius, weapon.SplashDamage, weapon.SplashKnockback);
            return;
        }

        SpawnProjectile(new Projectile
        {
            Owner = Projectile.Team.Player,
            Position = origin + forward * spawnDistance,
            Velocity = forward * weapon.ProjectileSpeed,
            Radius = 0.1f,
            SplashRadius = weapon.SplashRadius,
            SplashDamage = weapon.SplashDamage,
            SplashKnockback = weapon.SplashKnockback,
            Color = new Vector3(1f, 0.7f, 0.3f),
            TrailColor = SmokeColor,
            Size = 0.25f,
            LightRadius = 4f,
        });
    }

    // ===================================================================== projectiles

    public void SpawnProjectile(Projectile projectile) => Projectiles.Add(projectile);

    // Each frame, a projectile sweeps a short segment from where it was to
    // where it's going. Testing that whole segment (not just the end point)
    // means a fast rocket can't skip through a thin wall or an enemy between
    // two frames — the same "tunnelling" problem the platformer's per-axis
    // collision solves.
    private void UpdateProjectiles(float dt)
    {
        foreach (var p in Projectiles)
        {
            p.Life -= dt;
            if (p.Life <= 0f) { Detonate(p, null); continue; }

            p.Velocity.Y -= p.Gravity * dt;
            Vector3 step = p.Velocity * dt;
            float length = step.Length();
            if (length < 1e-6f) continue;
            Vector3 direction = step / length;

            float closest = length;
            Enemy? hitEnemy = null;
            bool hitPlayer = false;
            bool hitWorld = false;

            RayHit worldHit = Level.Raycast(p.Position, direction, length);
            if (worldHit.Hit) { closest = worldHit.Distance; hitWorld = true; }

            if (p.Owner == Projectile.Team.Player)
            {
                foreach (var enemy in Enemies)
                {
                    if (!enemy.IsAlive) continue;
                    float top = enemy.BaseY + enemy.Height * enemy.VisualScale.Y;
                    if (Geometry.RayCylinder(p.Position, direction, enemy.Position, enemy.Radius + p.Radius,
                            enemy.BaseY - p.Radius, top + p.Radius, out float t) && t < closest)
                    {
                        closest = t;
                        hitEnemy = enemy;
                        hitWorld = false;
                    }
                }
            }
            else if (Player.IsAlive &&
                     Geometry.RayCylinder(p.Position, direction, Player.Position, Player.Radius + p.Radius,
                         Player.FeetY - p.Radius, Player.FeetY + Player.BodyHeight + p.Radius, out float t) &&
                     t < closest)
            {
                closest = t;
                hitPlayer = true;
                hitWorld = false;
            }

            p.Position += direction * closest;

            // A smoke/ember trail: a couple of slow particles left behind each frame.
            Particles.Emit(p.Position, -direction, count: 1, p.TrailColor,
                spread: 0.4f, minSpeed: 0.1f, maxSpeed: 0.4f, life: 0.4f, size: p.Size * 0.6f, gravity: -0.3f);

            if (hitEnemy != null || hitPlayer || hitWorld)
            {
                // Back off a hair along the path so an explosion on a wall
                // isn't centred inside the wall (which would block its line of sight).
                p.Position -= direction * 0.05f;
                Detonate(p, hitEnemy, hitPlayer);
            }
        }

        Projectiles.RemoveAll(p => !p.IsAlive);
    }

    private void Detonate(Projectile p, Enemy? hitEnemy, bool hitPlayer = false)
    {
        p.IsAlive = false;

        if (p.SplashRadius > 0f)
        {
            Explode(p.Position, p.SplashRadius, p.SplashDamage, p.SplashKnockback);
            return;
        }

        if (hitEnemy != null)
        {
            var push = Vector2.Normalize(new Vector2(p.Velocity.X, p.Velocity.Z));
            hitEnemy.TakeDamage(p.Damage, push, 1.5f);
            EnemyHit?.Invoke();
        }
        if (hitPlayer)
            HurtPlayer(p.Damage, new Vector2(p.Position.X, p.Position.Z) - new Vector2(p.Velocity.X, p.Velocity.Z), knockback: 1.5f);

        Particles.Emit(p.Position, -Vector3.Normalize(p.Velocity), count: 10, p.Color,
            spread: 1f, minSpeed: 0.5f, maxSpeed: 2f, life: 0.35f, size: 0.06f);
    }

    // ===================================================================== damage

    // Splash damage: everything within `radius` (that the blast can actually
    // see — no damage through walls) takes damage that falls off linearly with
    // distance, and gets shoved away from the centre. The player is included:
    // that shove is a rocket jump.
    public void Explode(Vector3 center, float radius, int damage, float knockback)
    {
        var center2D = new Vector2(center.X, center.Z);

        Particles.Emit(center, Vector3.UnitY, count: 40, FireColor,
            spread: 2f, minSpeed: 1.5f, maxSpeed: 5f, life: 0.4f, size: 0.14f, gravity: 1f);
        Particles.Emit(center, Vector3.UnitY, count: 20, SmokeColor,
            spread: 1.5f, minSpeed: 0.3f, maxSpeed: 1.2f, life: 1.1f, size: 0.3f, gravity: -0.4f);
        AddFlashLight(center, new Vector3(1.6f, 0.9f, 0.4f), radius: radius * 3f, life: 0.35f);

        bool anyEnemyHit = false;
        foreach (var enemy in Enemies)
        {
            if (!enemy.IsAlive) continue;
            float distance = Geometry.DistanceToCylinderAxis(center, enemy.Position, enemy.BaseY, enemy.BaseY + enemy.Height);
            if (distance > radius + enemy.Radius || !Level.HasLineOfSight(center2D, enemy.Position)) continue;

            float falloff = 1f - Math.Clamp(distance / radius, 0f, 1f);
            Vector2 away = enemy.Position - center2D;
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX;
            enemy.TakeDamage(Math.Max(1, (int)MathF.Round(damage * falloff)), away, knockback * falloff);
            anyEnemyHit = true;
        }
        if (anyEnemyHit) EnemyHit?.Invoke();

        if (Player.IsAlive)
        {
            float distance = Geometry.DistanceToCylinderAxis(center, Player.Position, Player.FeetY, Player.FeetY + Player.BodyHeight);
            if (distance < radius && Level.HasLineOfSight(center2D, Player.Position))
            {
                float falloff = 1f - distance / radius;
                var playerCenter = new Vector3(Player.Position.X, Player.FeetY + Player.BodyHeight * 0.5f, Player.Position.Y);
                Vector3 away = playerCenter - center;
                away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Vector3.UnitY;
                Player.AddImpulse(away * knockback * falloff);
                Player.TakeDamage((int)MathF.Round(damage * falloff * SelfSplashDamageScale));
            }

            // Any explosion nearby shakes the camera, closer = harder.
            float shakeFalloff = 1f - Math.Clamp(Vector3.Distance(center, Player.EyePosition) / 8f, 0f, 1f);
            if (shakeFalloff > 0f) Player.Shake(0.07f * shakeFalloff, 0.3f);
        }
    }

    // A dust ring + camera shake, for heavy impacts (Brute slam, charge into a wall).
    public void Shockwave(Vector3 position, Vector3 color)
    {
        for (int i = 0; i < 16; i++)
        {
            float angle = i / 16f * MathF.Tau;
            var direction = new Vector3(MathF.Cos(angle), 0.15f, MathF.Sin(angle));
            Particles.Emit(position, direction, count: 1, color,
                spread: 0.1f, minSpeed: 2f, maxSpeed: 3f, life: 0.5f, size: 0.12f, gravity: 0.5f);
        }
        float falloff = 1f - Math.Clamp(Vector3.Distance(position, Player.EyePosition) / 7f, 0f, 1f);
        if (falloff > 0f) Player.Shake(0.05f * falloff, 0.25f);
    }

    public void HurtPlayer(int damage, Vector2 fromPosition, float knockback)
    {
        if (!Player.IsAlive) return;
        Vector2 away = Player.Position - fromPosition;
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.Zero;
        Player.AddImpulse(new Vector3(away.X, 0.25f, away.Y) * knockback);
        Player.TakeDamage(damage);
        Player.Shake(0.04f, 0.2f);
    }

    // ===================================================================== lights

    private void AddFlashLight(Vector3 position, Vector3 color, float radius, float life) =>
        _flashLights.Add(new DynamicLight { Position = position, Color = color, Radius = radius, Life = life, MaxLife = life });

    private void UpdateFlashLights(float dt)
    {
        for (int i = _flashLights.Count - 1; i >= 0; i--)
        {
            var light = _flashLights[i];
            light.Life -= dt;
            if (light.Life <= 0f) _flashLights.RemoveAt(i);
            else _flashLights[i] = light;
        }
    }

    // Every light that should shine this frame: explosion flashes + one per
    // projectile in flight. The renderer uploads the first few of these.
    public void CollectLights(List<DynamicLight> output)
    {
        output.Clear();
        output.AddRange(_flashLights);
        foreach (var p in Projectiles)
            output.Add(new DynamicLight { Position = p.Position, Color = p.Color, Radius = p.LightRadius });
    }

    private float Rand(float min, float max) => min + (float)_random.NextDouble() * (max - min);
}
