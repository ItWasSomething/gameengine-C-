using Silk.NET.OpenGL;

namespace Engine.Fps;

// Everything that makes one gun different from another, as plain data.
// Adding a new weapon = adding one more of these, no new classes. The shared
// Weapon class below runs the same ammo/cooldown/reload rules for all of them.
public sealed class WeaponDefinition
{
    public required string Name { get; init; }
    public bool Automatic { get; init; }        // hold to fire vs. one shot per click
    public int MagazineSize { get; init; }
    public float FireInterval { get; init; }    // seconds between shots
    public float ReloadDuration { get; init; }

    // Hitscan settings (ignored for projectile weapons).
    public int Damage { get; init; } = 1;       // per pellet
    public int Pellets { get; init; } = 1;      // >1 = shotgun
    public float Spread { get; init; }          // cone half-angle, radians
    public float Range { get; init; } = 40f;
    public float Knockback { get; init; } = 1.5f;

    // Projectile settings: if ProjectileSpeed > 0 the gun launches a
    // Projectile instead of an instant ray.
    public float ProjectileSpeed { get; init; }
    public float SplashRadius { get; init; }
    public int SplashDamage { get; init; }
    public float SplashKnockback { get; init; }

    // Feel.
    public float Recoil { get; init; }          // camera pitch kick, radians
    public float Shake { get; init; }
    public float KickDuration { get; init; } = 0.12f;
    public float MuzzleLight { get; init; } = 1f;

    // View model: how it's drawn on the HUD.
    public required Func<GL, Texture> CreateTexture { get; init; }
    public float MuzzleHeight { get; init; } = 48f / 64f; // flash position, fraction of sprite height
    public float ViewScale { get; init; } = 1f;

    // ------------------------------------------------------------------ the arsenal

    public static readonly WeaponDefinition Pistol = new()
    {
        Name = "Pistol",
        MagazineSize = 12, FireInterval = 0.14f, ReloadDuration = 1.0f,
        Damage = 2, Spread = 0.004f, Knockback = 1.5f,
        Recoil = 0.03f, Shake = 0f,
        CreateTexture = ProceduralTextures.Pistol,
    };

    // Double-barrel: two big shots, then a reload. Each pellet is its own
    // ray, so it shreds up close and barely tickles at range.
    public static readonly WeaponDefinition Shotgun = new()
    {
        Name = "Shotgun",
        MagazineSize = 2, FireInterval = 0.3f, ReloadDuration = 1.25f,
        Damage = 1, Pellets = 10, Spread = 0.09f, Knockback = 0.6f, // per pellet
        Recoil = 0.1f, Shake = 0.025f, KickDuration = 0.25f, MuzzleLight = 1.6f,
        CreateTexture = ProceduralTextures.Shotgun, MuzzleHeight = 46f / 64f, ViewScale = 1.15f,
    };

    public static readonly WeaponDefinition Smg = new()
    {
        Name = "SMG",
        Automatic = true,
        MagazineSize = 35, FireInterval = 0.075f, ReloadDuration = 1.4f,
        Damage = 1, Spread = 0.025f, Knockback = 0.6f,
        Recoil = 0.012f, KickDuration = 0.07f, MuzzleLight = 0.7f,
        CreateTexture = ProceduralTextures.Smg, MuzzleHeight = 50f / 64f,
    };

    // A slow projectile with splash damage. The splash hits YOU too (at
    // reduced damage) and shoves you — fire it at your feet to rocket-jump.
    public static readonly WeaponDefinition RocketLauncher = new()
    {
        Name = "Rocket Launcher",
        MagazineSize = 3, FireInterval = 0.8f, ReloadDuration = 1.8f,
        ProjectileSpeed = 15f, SplashRadius = 2.2f, SplashDamage = 12, SplashKnockback = 8f,
        Recoil = 0.06f, Shake = 0.02f, KickDuration = 0.3f, MuzzleLight = 1.2f,
        CreateTexture = ProceduralTextures.RocketLauncher, MuzzleHeight = 52f / 64f, ViewScale = 1.2f,
    };

    public static IReadOnlyList<WeaponDefinition> All { get; } = new[] { Pistol, Shotgun, Smg, RocketLauncher };
}

// One gun's live state — ammo, fire rate, reloading — driven by its
// definition. It doesn't know about rays, enemies or drawing: TryFire() just
// answers "did a shot happen?", and FpsWorld decides what the shot hits.
public sealed class Weapon
{
    public WeaponDefinition Definition { get; }
    public int Ammo { get; private set; }
    public bool IsReloading => _reloadTimer > 0f;
    public float ReloadProgress => IsReloading ? 1f - _reloadTimer / Definition.ReloadDuration : 0f;

    // 1 right after a shot, fading to 0. Read by the renderer for visuals.
    public float KickT => _kickTimer / Definition.KickDuration;
    public float FlashT => _flashTimer / FlashDuration;

    private const float FlashDuration = 0.05f;
    private float _cooldown;
    private float _reloadTimer;
    private float _kickTimer;
    private float _flashTimer;

    public Weapon(WeaponDefinition definition)
    {
        Definition = definition;
        Ammo = definition.MagazineSize;
    }

    public void Update(float dt)
    {
        _cooldown = MathF.Max(0f, _cooldown - dt);
        _kickTimer = MathF.Max(0f, _kickTimer - dt);
        _flashTimer = MathF.Max(0f, _flashTimer - dt);

        if (IsReloading)
        {
            _reloadTimer -= dt;
            if (_reloadTimer <= 0f)
            {
                _reloadTimer = 0f;
                Ammo = Definition.MagazineSize;
            }
        }
    }

    public bool TryFire()
    {
        if (IsReloading || _cooldown > 0f) return false;

        if (Ammo == 0)
        {
            StartReload(); // clicking on empty reloads automatically
            return false;
        }

        Ammo--;
        _cooldown = Definition.FireInterval;
        _kickTimer = Definition.KickDuration;
        _flashTimer = FlashDuration;
        return true;
    }

    public void StartReload()
    {
        if (IsReloading || Ammo == Definition.MagazineSize) return;
        _reloadTimer = Definition.ReloadDuration;
    }

    // Switching away interrupts a reload (it restarts when you come back),
    // the same as most shooters.
    public void CancelReload() => _reloadTimer = 0f;
}

// The weapons you're carrying plus the switch animation between them.
public sealed class Arsenal
{
    public float SwitchDuration = 0.3f;

    private readonly List<Weapon> _weapons = new();
    private int _pendingIndex = -1;
    private float _switchTimer;

    public int CurrentIndex { get; private set; }
    public Weapon Current => _weapons[CurrentIndex];
    public IReadOnlyList<Weapon> Weapons => _weapons;
    public bool IsSwitching => _switchTimer > 0f;

    // 0 = fully raised, 1 = fully lowered. Lowers during the first half of a
    // switch, swaps the gun at the bottom, raises during the second half.
    public float LowerAmount => IsSwitching ? 1f - MathF.Abs(1f - 2f * (_switchTimer / SwitchDuration)) : 0f;

    public Arsenal()
    {
        foreach (var definition in WeaponDefinition.All)
            _weapons.Add(new Weapon(definition));
    }

    public void SwitchTo(int index)
    {
        index = ((index % _weapons.Count) + _weapons.Count) % _weapons.Count; // wraps for mouse-wheel cycling
        if (index == CurrentIndex && !IsSwitching) return;
        Current.CancelReload();
        _pendingIndex = index;
        _switchTimer = SwitchDuration;
    }

    public void Cycle(int direction) => SwitchTo((_pendingIndex >= 0 ? _pendingIndex : CurrentIndex) + direction);

    public void Update(float dt)
    {
        foreach (var weapon in _weapons) weapon.Update(dt);
        if (!IsSwitching) return;

        float before = _switchTimer;
        _switchTimer = MathF.Max(0f, _switchTimer - dt);
        // Halfway through (gun fully lowered): swap which gun we're holding.
        if (before > SwitchDuration / 2f && _switchTimer <= SwitchDuration / 2f && _pendingIndex >= 0)
        {
            CurrentIndex = _pendingIndex;
            _pendingIndex = -1;
        }
        if (!IsSwitching && _pendingIndex >= 0) { CurrentIndex = _pendingIndex; _pendingIndex = -1; }
    }

    // You can't shoot mid-switch.
    public bool TryFire() => !IsSwitching && Current.TryFire();
}
