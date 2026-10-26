using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Fps;

// A tiny CPU-side RGBA image you can paint into with code. Row 0 is the
// BOTTOM row (y goes up), matching GL and the rest of the engine.
public sealed class Pixmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public Pixmap(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[width * height * 4]; // starts fully transparent
    }

    public void Set(int x, int y, Vector3 color, float alpha = 1f)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = (y * Width + x) * 4;
        Data[i + 0] = ToByte(color.X);
        Data[i + 1] = ToByte(color.Y);
        Data[i + 2] = ToByte(color.Z);
        Data[i + 3] = ToByte(alpha);
    }

    public void Clear(int x, int y) => Set(x, y, Vector3.Zero, 0f);

    // Inclusive-exclusive like most graphics APIs: x0..x1-1, y0..y1-1.
    public void FillRect(int x0, int y0, int x1, int y1, Vector3 color)
    {
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
            Set(x, y, color);
    }

    public void FillEllipse(float cx, float cy, float rx, float ry, Vector3 color)
    {
        for (int y = (int)(cy - ry); y <= (int)(cy + ry); y++)
        for (int x = (int)(cx - rx); x <= (int)(cx + rx); x++)
        {
            float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
            if (dx * dx + dy * dy <= 1f) Set(x, y, color);
        }
    }

    public Texture ToTexture(GL gl, bool repeat = false) => new(gl, Width, Height, Data, repeat);

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}

// All the FPS mode's art, generated from code at startup. There are no image
// files for this mode yet on purpose: it keeps the whole thing in a few
// readable functions, and each one is a small lesson in how textures are
// "just arrays of numbers". Swap any of these for a PNG later via
// `new Texture(gl, path)` — nothing else needs to change.
public static class ProceduralTextures
{
    // Deterministic pseudo-random 0..1 from integer coordinates. Same inputs
    // always give the same output, so the "random" variation is stable every
    // run (unlike System.Random, which would need a fixed seed + call order).
    public static float Hash(int x, int y, int seed = 0)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 982451653);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (float)uint.MaxValue;
        }
    }

    public static Texture White(GL gl)
    {
        var p = new Pixmap(1, 1);
        p.Set(0, 0, Vector3.One);
        return p.ToTexture(gl);
    }

    // Red bricks, 32x16 px each, every other row shifted half a brick.
    // 64x64 = exactly 2 bricks across and 4 rows up, so the edges line up
    // when the texture tiles.
    public static Texture Brick(GL gl)
    {
        var p = new Pixmap(64, 64);
        var brick = new Vector3(0.55f, 0.24f, 0.17f);
        var mortar = new Vector3(0.33f, 0.31f, 0.28f);

        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int row = y / 16;
            int shiftedX = (x + (row % 2) * 16) % 64;
            bool isMortar = y % 16 < 2 || shiftedX % 32 < 2;
            float grain = 0.88f + 0.2f * Hash(x, y, 1);

            if (isMortar)
                p.Set(x, y, mortar * grain);
            else
                p.Set(x, y, brick * (0.75f + 0.4f * Hash(shiftedX / 32, row, 2)) * grain);
        }
        return p.ToTexture(gl, repeat: true);
    }

    // Big mossy grey-green blocks.
    public static Texture Stone(GL gl)
    {
        var p = new Pixmap(64, 64);
        var stone = new Vector3(0.36f, 0.39f, 0.36f);
        var moss = new Vector3(0.22f, 0.36f, 0.18f);
        var gap = new Vector3(0.12f, 0.13f, 0.12f);

        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int row = y / 32;
            int shiftedX = (x + (row % 2) * 16) % 64;
            bool isGap = y % 32 < 2 || shiftedX % 32 < 2;
            float grain = 0.85f + 0.25f * Hash(x, y, 3);

            if (isGap) { p.Set(x, y, gap); continue; }

            float blockTone = 0.8f + 0.3f * Hash(shiftedX / 32, row, 4);
            // Moss collects toward the bottom of each block.
            float mossAmount = MathF.Max(0f, 0.6f - (y % 32) / 32f) * Hash(x / 3, y / 3, 5);
            p.Set(x, y, Vector3.Lerp(stone * blockTone, moss, mossAmount) * grain);
        }
        return p.ToTexture(gl, repeat: true);
    }

    // Dark flagstones, 4 per tile.
    public static Texture Floor(GL gl)
    {
        var p = new Pixmap(64, 64);
        var slab = new Vector3(0.30f, 0.28f, 0.27f);
        var grout = new Vector3(0.12f, 0.11f, 0.10f);

        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            bool isGrout = x % 32 == 0 || y % 32 == 0;
            float tone = 0.8f + 0.3f * Hash(x / 32, y / 32, 6);
            float speckle = 0.85f + 0.25f * Hash(x, y, 7);
            p.Set(x, y, isGrout ? grout : slab * tone * speckle);
        }
        return p.ToTexture(gl, repeat: true);
    }

    // Wooden planks running along one axis.
    public static Texture Ceiling(GL gl)
    {
        var p = new Pixmap(64, 64);
        var wood = new Vector3(0.30f, 0.20f, 0.12f);
        var seam = new Vector3(0.08f, 0.05f, 0.03f);

        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int plank = x / 16;
            if (x % 16 == 0) { p.Set(x, y, seam); continue; }
            float tone = 0.8f + 0.35f * Hash(plank, 0, 8);
            // Wood grain: a slow wave along the plank, offset per plank.
            float grain = 0.92f + 0.08f * MathF.Sin(y * 0.6f + Hash(plank, 1, 9) * 10f + x * 0.3f);
            p.Set(x, y, wood * tone * grain);
        }
        return p.ToTexture(gl, repeat: true);
    }

    // The Imp: a horned purple imp with glowing eyes. 32x40, transparent
    // background — that transparency is what the alpha cutout in world.frag
    // throws away so it doesn't hide the wall behind it.
    public static Texture Imp(GL gl)
    {
        var p = new Pixmap(32, 40);
        var bodyDark = new Vector3(0.25f, 0.08f, 0.30f);
        var bodyLight = new Vector3(0.55f, 0.22f, 0.60f);
        var horn = new Vector3(0.85f, 0.80f, 0.65f);
        var eye = new Vector3(1f, 0.9f, 0.25f);
        var mouth = new Vector3(0.25f, 0.02f, 0.04f);

        // Body: an ellipse, lit from the upper left, with a wavy ghostly hem.
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            float dx = (x + 0.5f - 16f) / 12f, dy = (y + 0.5f - 15f) / 14f;
            if (dx * dx + dy * dy > 1f) continue;
            if (y < 3f + 2f * MathF.Sin(x * 0.9f)) continue; // hem
            float light = Math.Clamp(1.1f - Vector2.Distance(new(x, y), new(11f, 22f)) / 16f, 0f, 1f);
            p.Set(x, y, Vector3.Lerp(bodyDark, bodyLight, light));
        }

        // Horns: columns that narrow as they rise and curve outward.
        for (int y = 24; y < 39; y++)
        {
            int width = Math.Max(1, (39 - y) / 4);
            int lean = (y - 24) / 4;
            for (int i = 0; i < width; i++)
            {
                p.Set(8 - lean + i, y, horn);
                p.Set(23 + lean - i, y, horn);
            }
        }

        // Glowing eyes with a dark slit pupil.
        foreach (int cx in new[] { 11, 20 })
        {
            p.FillRect(cx - 2, 18, cx + 3, 22, eye);
            p.FillRect(cx, 18, cx + 1, 22, mouth);
        }

        // Toothy mouth.
        p.FillRect(10, 10, 22, 13, mouth);
        for (int x = 10; x < 22; x += 2) p.Set(x, 12, Vector3.One);

        return p.ToTexture(gl);
    }

    // The player's pistol, seen from behind (first-person view model). Drawn
    // on top of everything in screen space, like any 2D HUD sprite.
    public static Texture Pistol(GL gl)
    {
        var p = new Pixmap(64, 64);
        var sleeve = new Vector3(0.18f, 0.22f, 0.18f);
        var skin = new Vector3(0.85f, 0.64f, 0.50f);
        var skinShadow = new Vector3(0.62f, 0.44f, 0.34f);
        var metal = new Vector3(0.34f, 0.35f, 0.40f);
        var metalLight = new Vector3(0.55f, 0.56f, 0.62f);
        var metalDark = new Vector3(0.16f, 0.16f, 0.19f);

        p.FillRect(18, 0, 46, 10, sleeve);
        p.FillRect(21, 8, 43, 23, skin);
        p.FillRect(36, 8, 43, 23, skinShadow);          // right side of the hand in shadow
        for (int x = 23; x < 41; x += 5) p.FillRect(x, 20, x + 1, 23, skinShadow); // knuckle gaps

        p.FillRect(24, 22, 40, 42, metal);              // slide, seen from the back
        p.FillRect(24, 22, 26, 42, metalLight);         // lit left edge
        p.FillRect(38, 22, 40, 42, metalDark);          // shaded right edge
        p.FillRect(24, 40, 40, 42, metalLight);         // top edge
        p.FillRect(29, 26, 35, 31, metalDark);          // hammer

        p.FillRect(25, 42, 29, 46, metalDark);          // rear sight, left post
        p.FillRect(35, 42, 39, 46, metalDark);          // rear sight, right post
        p.FillRect(31, 42, 33, 47, metalLight);         // front sight, seen through the notch

        return p.ToTexture(gl);
    }

    // The Spitter: a fat green slug with eye stalks and a gaping mouth
    // facing you. 40x32.
    public static Texture Spitter(GL gl)
    {
        var p = new Pixmap(40, 32);
        var dark = new Vector3(0.18f, 0.35f, 0.10f);
        var light = new Vector3(0.45f, 0.75f, 0.25f);

        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 40; x++)
        {
            float dx = (x + 0.5f - 20f) / 19f, dy = (y + 0.5f - 11f) / 11f;
            if (dx * dx + dy * dy > 1f) continue;
            float shade = Math.Clamp(0.3f + (y / 22f) * 0.8f, 0f, 1f);
            var color = Vector3.Lerp(dark, light, shade);
            if (Hash(x / 3, y / 3, 20) > 0.8f) color *= 0.7f; // blotchy spots
            p.Set(x, y, color);
        }

        // Eye stalks with yellow eyes.
        foreach (var (baseX, topX) in new[] { (15, 13), (25, 27) })
        {
            for (int y = 18; y < 27; y++)
                p.Set(baseX + (topX - baseX) * (y - 18) / 9, y, dark);
            p.FillEllipse(topX + 0.5f, 28f, 2.5f, 2.5f, new Vector3(1f, 0.95f, 0.3f));
            p.Set(topX, 28, Vector3.Zero);
        }

        // Mouth with acid glowing inside.
        p.FillEllipse(20f, 10f, 7f, 5f, new Vector3(0.1f, 0.05f, 0.05f));
        p.FillEllipse(20f, 9f, 4f, 2.5f, new Vector3(0.6f, 1f, 0.2f));
        return p.ToTexture(gl);
    }

    // The Brute: a hulking armoured brawler with huge fists. 48x56.
    public static Texture Brute(GL gl)
    {
        var p = new Pixmap(48, 56);
        var skin = new Vector3(0.48f, 0.30f, 0.26f);
        var skinDark = new Vector3(0.30f, 0.17f, 0.15f);
        var armor = new Vector3(0.38f, 0.40f, 0.44f);
        var armorLight = new Vector3(0.60f, 0.62f, 0.66f);
        var eye = new Vector3(1f, 0.25f, 0.1f);

        p.FillRect(12, 0, 20, 12, skinDark);                 // legs
        p.FillRect(28, 0, 36, 12, skinDark);
        p.FillEllipse(24f, 26f, 18f, 17f, skin);             // torso
        p.FillRect(10, 30, 38, 38, armor);                   // chest plate
        p.FillRect(10, 36, 38, 38, armorLight);
        p.FillEllipse(7f, 34f, 6f, 5f, armor);               // shoulder pads
        p.FillEllipse(41f, 34f, 6f, 5f, armor);
        for (int i = 0; i < 3; i++)                          // shoulder spikes
        {
            p.FillRect(4 + i * 3, 39, 5 + i * 3, 43 - i, armorLight);
            p.FillRect(38 + i * 3, 39, 39 + i * 3, 41 + i, armorLight);
        }
        p.FillEllipse(5f, 16f, 5f, 8f, skinDark);            // arms
        p.FillEllipse(43f, 16f, 5f, 8f, skinDark);
        p.FillEllipse(5f, 8f, 5.5f, 5f, skin);               // fists
        p.FillEllipse(43f, 8f, 5.5f, 5f, skin);
        p.FillRect(18, 40, 30, 51, skin);                    // head
        p.FillRect(18, 48, 30, 51, armor);                   // helmet rim
        p.FillRect(19, 44, 23, 46, eye);
        p.FillRect(25, 44, 29, 46, eye);
        p.FillRect(20, 41, 28, 42, skinDark);                // grim mouth
        return p.ToTexture(gl);
    }

    // The Wisp: a glowing cyan orb with an eye and ragged wings. 32x32.
    public static Texture Wisp(GL gl)
    {
        var p = new Pixmap(32, 32);
        var wing = new Vector3(0.25f, 0.45f, 0.6f);

        // Wings: rows of pixels that get shorter toward the tips.
        for (int y = 10; y < 24; y++)
        {
            int length = 12 - Math.Abs(y - 17) - (Hash(y, 0, 30) > 0.6f ? 2 : 0);
            for (int i = 0; i < length; i++)
            {
                p.Set(10 - i, y, wing);
                p.Set(21 + i, y, wing);
            }
        }

        // Core: brighter toward the middle (fake glow).
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            float d = Vector2.Distance(new(x + 0.5f, y + 0.5f), new(16f, 16f));
            if (d > 9f) continue;
            float t = 1f - d / 9f;
            p.Set(x, y, Vector3.Lerp(new Vector3(0.2f, 0.6f, 0.8f), new Vector3(0.9f, 1f, 1f), t * t));
        }

        p.FillEllipse(16f, 16f, 3.5f, 3f, new Vector3(0.05f, 0.1f, 0.2f)); // eye
        p.FillEllipse(16f, 16f, 1.2f, 1.2f, new Vector3(1f, 0.3f, 0.3f));
        return p.ToTexture(gl);
    }

    // ---------------------------------------------------------------- view models

    private static readonly Vector3 Skin = new(0.85f, 0.64f, 0.50f);
    private static readonly Vector3 SkinShadow = new(0.62f, 0.44f, 0.34f);
    private static readonly Vector3 Sleeve = new(0.18f, 0.22f, 0.18f);

    private static void DrawHand(Pixmap p, int x0, int x1)
    {
        p.FillRect(x0 - 3, 0, x1 + 3, 10, Sleeve);
        p.FillRect(x0, 8, x1, 23, Skin);
        p.FillRect(x1 - 6, 8, x1, 23, SkinShadow);
    }

    // Double-barrel shotgun: two fat barrels side by side over a wooden stock.
    public static Texture Shotgun(GL gl)
    {
        var p = new Pixmap(64, 64);
        var wood = new Vector3(0.45f, 0.27f, 0.13f);
        var woodDark = new Vector3(0.28f, 0.16f, 0.08f);
        var metal = new Vector3(0.30f, 0.30f, 0.34f);
        var metalLight = new Vector3(0.55f, 0.55f, 0.60f);
        var hole = new Vector3(0.04f, 0.04f, 0.05f);

        DrawHand(p, 20, 44);
        p.FillRect(19, 18, 45, 30, wood);                    // fore-end
        p.FillRect(19, 18, 45, 20, woodDark);
        foreach (int cx in new[] { 26, 38 })
        {
            p.FillRect(cx - 6, 28, cx + 6, 44, metal);       // barrel
            p.FillRect(cx - 6, 28, cx - 4, 44, metalLight);  // highlight
            p.FillEllipse(cx, 44f, 6f, 3f, metal);           // rounded muzzle end
            p.FillEllipse(cx, 44f, 3.5f, 1.8f, hole);
        }
        return p.ToTexture(gl);
    }

    // SMG: a slim boxy body with a magazine sticking down to the left.
    public static Texture Smg(GL gl)
    {
        var p = new Pixmap(64, 64);
        var body = new Vector3(0.22f, 0.23f, 0.26f);
        var bodyLight = new Vector3(0.42f, 0.43f, 0.48f);
        var mag = new Vector3(0.15f, 0.15f, 0.17f);

        p.FillRect(18, 6, 25, 30, mag);                      // magazine
        DrawHand(p, 26, 42);
        p.FillRect(26, 20, 39, 46, body);                    // receiver
        p.FillRect(26, 20, 28, 46, bodyLight);
        p.FillRect(26, 44, 39, 46, bodyLight);
        p.FillRect(31, 46, 34, 50, bodyLight);               // front sight post
        p.FillRect(27, 30, 38, 31, mag);                     // panel line
        return p.ToTexture(gl);
    }

    // Rocket launcher: a big olive tube with the dark opening on top.
    public static Texture RocketLauncher(GL gl)
    {
        var p = new Pixmap(64, 64);
        var olive = new Vector3(0.33f, 0.36f, 0.20f);
        var oliveLight = new Vector3(0.50f, 0.54f, 0.32f);
        var oliveDark = new Vector3(0.20f, 0.22f, 0.12f);
        var hole = new Vector3(0.03f, 0.03f, 0.03f);

        DrawHand(p, 22, 42);
        p.FillRect(16, 16, 48, 50, olive);                   // tube
        p.FillRect(16, 16, 20, 50, oliveLight);
        p.FillRect(44, 16, 48, 50, oliveDark);
        p.FillRect(16, 26, 48, 28, oliveDark);               // band
        p.FillEllipse(32f, 50f, 16f, 5f, oliveLight);        // rim
        p.FillEllipse(32f, 50f, 12f, 3.5f, hole);            // bore
        p.FillRect(48, 30, 54, 40, oliveDark);               // sight box
        p.FillRect(49, 36, 53, 39, new Vector3(0.9f, 0.3f, 0.2f));
        return p.ToTexture(gl);
    }
}
