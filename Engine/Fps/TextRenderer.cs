using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Engine.Fps;

public enum TextAlign { Left, Center, Right }

// The engine's first text: a tiny 5x7 pixel font, hand-drawn below as rows of
// '#' and '.', baked at startup into ONE texture (a "font atlas") with every
// letter side by side. Drawing a string is then just drawing one quad per
// letter, each showing a different small rectangle of that atlas (text.vert's
// uUvRect). This is the same trick every bitmap-font renderer uses — real
// fonts (TTF) just rasterise their atlas from curves instead of from strings.
//
// Only uppercase is defined; lowercase input is upper-cased. Characters that
// aren't in the table are drawn as blanks, so a typo can't crash the game.
public sealed class TextRenderer : IDisposable
{
    public const int GlyphWidth = 5, GlyphHeight = 7;
    // Each atlas cell has a 1px empty border around the glyph, so sampling
    // right at a glyph's edge can never pick up a neighbouring letter.
    private const int CellWidth = GlyphWidth + 2, CellHeight = GlyphHeight + 2;
    private const int AtlasColumns = 16;

    // Rows are listed top to bottom, the way you'd read them.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
        ['C'] = new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." },
        ['D'] = new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
        ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
        ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
        ['G'] = new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####" },
        ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        ['I'] = new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['J'] = new[] { "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.." },
        ['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
        ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
        ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
        ['N'] = new[] { "#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#" },
        ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
        ['Q'] = new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" },
        ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
        ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
        ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
        ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['V'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." },
        ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#." },
        ['X'] = new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" },
        ['Y'] = new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." },
        ['Z'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "..#.." },
        [','] = new[] { ".....", ".....", ".....", ".....", ".....", "..#..", ".#..." },
        [':'] = new[] { ".....", "..#..", ".....", ".....", ".....", "..#..", "....." },
        ['-'] = new[] { ".....", ".....", ".....", ".###.", ".....", ".....", "....." },
        ['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
        ['!'] = new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." },
        ['?'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.." },
        ['/'] = new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." },
        ['('] = new[] { "...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#." },
        [')'] = new[] { ".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#..." },
        ['>'] = new[] { ".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..." },
        ['<'] = new[] { "...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#." },
        ['\''] = new[] { "..#..", "..#..", ".....", ".....", ".....", ".....", "....." },
    };

    private static readonly float[] Vertices =
    {
        // position       // texcoord
        -0.5f,  0.5f,     0f, 1f,
         0.5f,  0.5f,     1f, 1f,
         0.5f, -0.5f,     1f, 0f,
        -0.5f, -0.5f,     0f, 0f,
    };
    private static readonly uint[] Indices = { 0, 1, 2, 2, 3, 0 };

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Texture _atlas;
    private readonly Dictionary<char, Vector4> _uvRects = new();
    private readonly uint _vao, _vbo, _ebo;

    public unsafe TextRenderer(GL gl, Shader shader)
    {
        _gl = gl;
        _shader = shader;
        _atlas = BuildAtlas(gl, _uvRects);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* v = Vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* i = Indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        const uint stride = 4 * sizeof(float);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
    }

    // Paints every glyph into one Pixmap, remembering where each one landed.
    private static Texture BuildAtlas(GL gl, Dictionary<char, Vector4> uvRects)
    {
        int rows = (Glyphs.Count + AtlasColumns - 1) / AtlasColumns;
        var pixmap = new Pixmap(AtlasColumns * CellWidth, rows * CellHeight);

        int index = 0;
        foreach (var (character, glyphRows) in Glyphs)
        {
            int cellX = (index % AtlasColumns) * CellWidth + 1;
            int cellY = (index / AtlasColumns) * CellHeight + 1;

            for (int row = 0; row < GlyphHeight; row++)
            for (int column = 0; column < GlyphWidth; column++)
            {
                // Pixmap row 0 is the bottom, but the strings list the top
                // row first, so flip the row index.
                if (glyphRows[row][column] == '#')
                    pixmap.Set(cellX + column, cellY + (GlyphHeight - 1 - row), Vector3.One);
            }

            uvRects[character] = new Vector4(
                (float)cellX / pixmap.Width, (float)cellY / pixmap.Height,
                (float)GlyphWidth / pixmap.Width, (float)GlyphHeight / pixmap.Height);
            index++;
        }

        return pixmap.ToTexture(gl);
    }

    // One glyph is 5 pixels wide plus 1 pixel of spacing, times the scale.
    public static float Measure(string text, float scale) =>
        text.Length == 0 ? 0f : (text.Length * (GlyphWidth + 1) - 1) * scale;

    public static float LineHeight(float scale) => GlyphHeight * scale;

    // `position` is the BASELINE (bottom of the letters) at the left edge,
    // centre or right edge depending on `align`. Use whole-number scales —
    // 2, 3, 4 — so each font pixel covers an exact block of screen pixels and
    // stays perfectly sharp.
    public unsafe void Draw(string text, Vector2 position, float scale, Vector4 color, Matrix4x4 projection,
        TextAlign align = TextAlign.Left, bool shadow = true)
    {
        float x = align switch
        {
            TextAlign.Center => position.X - Measure(text, scale) / 2f,
            TextAlign.Right => position.X - Measure(text, scale),
            _ => position.X,
        };
        // Snap to whole pixels, otherwise a half-pixel offset smears every
        // letter across two screen pixels.
        x = MathF.Round(x);
        float y = MathF.Round(position.Y);

        _shader.Use();
        _shader.SetUniform("uProjection", projection);
        _shader.SetUniform("uTexture", 0);
        _atlas.Bind(TextureUnit.Texture0);
        _gl.BindVertexArray(_vao);

        // A dark copy one font-pixel down-right first: cheap drop shadow that
        // keeps text readable over a busy 3D background.
        if (shadow)
            DrawRun(text, x + scale, y - scale, scale, new Vector4(0f, 0f, 0f, color.W * 0.75f));
        DrawRun(text, x, y, scale, color);
    }

    private unsafe void DrawRun(string text, float left, float baseline, float scale, Vector4 color)
    {
        _shader.SetUniform("uTint", color);
        var size = new Vector2D<float>(GlyphWidth * scale, GlyphHeight * scale);
        float advance = (GlyphWidth + 1) * scale;

        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            if (_uvRects.TryGetValue(c, out var uv))
            {
                // Sprite-style quads are positioned by their centre.
                _shader.SetUniform("uPosition", new Vector2D<float>(left + size.X / 2f, baseline + size.Y / 2f));
                _shader.SetUniform("uSize", size);
                _shader.SetUniform("uUvRect", uv);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)Indices.Length, DrawElementsType.UnsignedInt, null);
            }
            left += advance;
        }
    }

    public void Dispose()
    {
        _atlas.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
