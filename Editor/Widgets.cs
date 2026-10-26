using System.Numerics;
using Engine.Core;
using ImGuiNET;

namespace Editor;

public enum Icon { Play, Pause, Stop, Step, Move, Rotate, Scale, Add }

// Small custom widgets drawn with ImGui's draw list. Icons are shapes
// (triangles, squares, circles) rather than font glyphs, so there's no icon
// font to ship and they stay crisp at any size.
public static class Widgets
{
    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    // Prefab instances are the one thing drawn in pure white; everything else
    // is a shade of gray, so prefabs stand out without needing a color.
    public static readonly Vector4 PrefabColor = EditorTheme.Rgb(0xFFFFFF);

    // A square button with a drawn icon. `active` highlights it (e.g. the
    // Play button while playing).
    public static bool IconButton(string id, Icon icon, bool active, string tooltip, Vector4? tint = null, float size = 26)
    {
        var pos = ImGui.GetCursorScreenPos();
        bool pressed = ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();

        var bg = active ? EditorTheme.WithAlpha(EditorTheme.Accent, 0.25f)
            : held ? new Vector4(1, 1, 1, 0.12f)
            : hovered ? new Vector4(1, 1, 1, 0.07f) : Vector4.Zero;
        if (bg.W > 0) dl.AddRectFilled(pos, pos + new Vector2(size), U32(bg));

        var color = U32(tint ?? (active ? EditorTheme.Accent : EditorTheme.Text));
        var c = pos + new Vector2(size / 2);
        float r = size * 0.26f;
        switch (icon)
        {
            case Icon.Play:
                dl.AddTriangleFilled(c + new Vector2(-r * 0.75f, -r), c + new Vector2(-r * 0.75f, r), c + new Vector2(r, 0), color);
                break;
            case Icon.Pause:
                dl.AddRectFilled(c + new Vector2(-r * 0.8f, -r), c + new Vector2(-r * 0.25f, r), color, 1);
                dl.AddRectFilled(c + new Vector2(r * 0.25f, -r), c + new Vector2(r * 0.8f, r), color, 1);
                break;
            case Icon.Stop:
                dl.AddRectFilled(c - new Vector2(r * 0.85f), c + new Vector2(r * 0.85f), color, 2);
                break;
            case Icon.Step:
                dl.AddTriangleFilled(c + new Vector2(-r, -r), c + new Vector2(-r, r), c + new Vector2(r * 0.4f, 0), color);
                dl.AddRectFilled(c + new Vector2(r * 0.5f, -r), c + new Vector2(r * 0.95f, r), color, 1);
                break;
            case Icon.Move:
                dl.AddLine(c - new Vector2(r, 0), c + new Vector2(r, 0), color, 2);
                dl.AddLine(c - new Vector2(0, r), c + new Vector2(0, r), color, 2);
                dl.AddTriangleFilled(c + new Vector2(r + 3, 0), c + new Vector2(r - 2, -4), c + new Vector2(r - 2, 4), color);
                dl.AddTriangleFilled(c - new Vector2(0, r + 3), c - new Vector2(-4, r - 2), c - new Vector2(4, r - 2), color);
                break;
            case Icon.Rotate:
                dl.PathArcTo(c, r, 0.4f, MathF.PI * 1.75f, 20);
                dl.PathStroke(color, ImDrawFlags.None, 2);
                dl.AddTriangleFilled(c + new Vector2(r + 4, -1), c + new Vector2(r - 4, -1), c + new Vector2(r, 5), color);
                break;
            case Icon.Scale:
                dl.AddRect(c - new Vector2(r), c + new Vector2(r * 0.2f), color, 1, ImDrawFlags.None, 1.5f);
                dl.AddRectFilled(c, c + new Vector2(r), color, 1);
                break;
            case Icon.Add:
                dl.AddLine(c - new Vector2(r, 0), c + new Vector2(r, 0), color, 2.5f);
                dl.AddLine(c - new Vector2(0, r), c + new Vector2(0, r), color, 2.5f);
                break;
        }

        if (hovered && tooltip.Length > 0) ImGui.SetTooltip(tooltip);
        return pressed;
    }

    // ---- Icons for GameObjects and components -------------------------------
    //
    // A GameObject's icon reflects its most telling component, like Unity:
    // a camera, a light, a mesh — or a blue cube for prefab instances.

    private enum Shape { Cube, HollowCube, Circle, Camera, Sun, Script, Sky }

    private static (Shape, Vector4) Describe(Type componentType)
    {
        if (componentType == typeof(Camera)) return (Shape.Camera, EditorTheme.Rgb(0xC8C8C8));
        if (componentType == typeof(Light)) return (Shape.Sun, EditorTheme.Rgb(0xE0E0E0));
        if (componentType == typeof(MeshRenderer) || componentType == typeof(MeshFilter)) return (Shape.Cube, EditorTheme.Rgb(0xB4B4B4));
        if (componentType == typeof(SkyEnvironment)) return (Shape.Sky, EditorTheme.Rgb(0x9A9A9A));
        if (componentType == typeof(Transform)) return (Shape.HollowCube, EditorTheme.TextDim);
        if (componentType == typeof(MissingComponent)) return (Shape.Script, EditorTheme.Error);
        return (Shape.Script, EditorTheme.Rgb(0xA8A8A8));
    }

    public static Vector4 ComponentColor(Type componentType) => Describe(componentType).Item2;

    public static Vector4 GameObjectColor(GameObject go)
    {
        if (go.prefabAssetPath != null) return PrefabColor;
        return Describe(MainComponent(go)).Item2;
    }

    private static Type MainComponent(GameObject go)
    {
        foreach (var t in new[] { typeof(Camera), typeof(Light), typeof(MeshRenderer), typeof(SkyEnvironment) })
            if (go.GetComponent(t) != null) return t;
        return typeof(Transform);
    }

    public static void GameObjectIcon(GameObject go, float size = 13)
    {
        if (go.prefabAssetPath != null) DrawShape(Shape.Cube, PrefabColor, size);
        else
        {
            var (shape, color) = Describe(MainComponent(go));
            DrawShape(shape, color, size);
        }
    }

    public static void ComponentIcon(Type type, float size = 14)
    {
        var (shape, color) = Describe(type);
        DrawShape(shape, color, size);
    }

    public static void PrefabIcon(float size = 13) => DrawShape(Shape.Cube, PrefabColor, size);

    private static void DrawShape(Shape shape, Vector4 color4, float size)
    {
        var pos = ImGui.GetCursorScreenPos();
        float lineH = MathF.Max(ImGui.GetTextLineHeight(), size);
        var c = pos + new Vector2(size / 2, lineH / 2);
        var color = U32(color4);
        var dl = ImGui.GetWindowDrawList();
        float r = size * 0.4f;

        switch (shape)
        {
            case Shape.Cube:
                // A tiny isometric cube: top face lighter than the sides.
                var top = new[] { c + new Vector2(0, -r), c + new Vector2(r, -r * 0.5f), c + new Vector2(0, 0), c + new Vector2(-r, -r * 0.5f) };
                dl.AddQuadFilled(top[0], top[1], top[2], top[3], U32(color4 with { W = 1 } * new Vector4(1.15f, 1.15f, 1.15f, 1)));
                dl.AddQuadFilled(c + new Vector2(-r, -r * 0.5f), c, c + new Vector2(0, r), c + new Vector2(-r, r * 0.5f), color);
                dl.AddQuadFilled(c, c + new Vector2(r, -r * 0.5f), c + new Vector2(r, r * 0.5f), c + new Vector2(0, r), U32(color4 * new Vector4(0.75f, 0.75f, 0.75f, 1)));
                break;
            case Shape.HollowCube:
                dl.AddQuad(c + new Vector2(0, -r), c + new Vector2(r, -r * 0.5f), c + new Vector2(0, 0), c + new Vector2(-r, -r * 0.5f), color, 1.2f);
                dl.AddLine(c + new Vector2(-r, -r * 0.5f), c + new Vector2(-r, r * 0.5f), color, 1.2f);
                dl.AddLine(c + new Vector2(r, -r * 0.5f), c + new Vector2(r, r * 0.5f), color, 1.2f);
                dl.AddLine(c, c + new Vector2(0, r), color, 1.2f);
                dl.AddLine(c + new Vector2(-r, r * 0.5f), c + new Vector2(0, r), color, 1.2f);
                dl.AddLine(c + new Vector2(r, r * 0.5f), c + new Vector2(0, r), color, 1.2f);
                break;
            case Shape.Circle:
                dl.AddCircleFilled(c, r, color);
                break;
            case Shape.Sun:
                dl.AddCircleFilled(c, r * 0.55f, color);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.PI / 4;
                    var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                    dl.AddLine(c + d * r * 0.8f, c + d * r * 1.15f, color, 1.2f);
                }
                break;
            case Shape.Camera:
                dl.AddRectFilled(c + new Vector2(-r, -r * 0.55f), c + new Vector2(r * 0.35f, r * 0.55f), color, 1.5f);
                dl.AddTriangleFilled(c + new Vector2(r * 0.3f, 0), c + new Vector2(r, -r * 0.6f), c + new Vector2(r, r * 0.6f), color);
                break;
            case Shape.Script:
                dl.AddRectFilled(c + new Vector2(-r * 0.75f, -r), c + new Vector2(r * 0.75f, r), color, 1.5f);
                var ink = U32(EditorTheme.WithAlpha(new Vector4(0, 0, 0, 1), 0.55f));
                for (int i = 0; i < 3; i++)
                    dl.AddLine(c + new Vector2(-r * 0.45f, -r * 0.5f + i * r * 0.5f), c + new Vector2(r * 0.45f, -r * 0.5f + i * r * 0.5f), ink, 1);
                break;
            case Shape.Sky:
                dl.AddCircle(c, r, color, 12, 1.5f);
                dl.AddLine(c - new Vector2(r, 0), c + new Vector2(r, 0), color, 1.5f);
                break;
        }

        ImGui.Dummy(new Vector2(size, lineH));
    }

    // "MeshRenderer" → "Mesh Renderer".
    public static string NiceName(string name)
    {
        name = name.TrimStart('_');
        if (name.StartsWith("m_") && name.Length > 2) name = name[2..];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (i == 0) { sb.Append(char.ToUpperInvariant(ch)); continue; }
            bool boundary = char.IsUpper(ch) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))
                            || (char.IsDigit(ch) && !char.IsDigit(name[i - 1]));
            if (boundary) sb.Append(' ');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

// What's being dragged. ImGui payloads are raw bytes, so the actual object
// travels through these static fields (only one drag can happen at a time).
public static class DragPayload
{
    public const string GameObjectType = "GAMEOBJECT";
    public const string AssetType = "ASSET";

    public static GameObject? GameObject;
    public static string? AssetPath;   // project-relative, e.g. "Assets/Coin.prefab"

    public static unsafe bool Accept(string type)
    {
        var payload = ImGui.AcceptDragDropPayload(type);
        return payload.NativePtr != null;
    }

    // Peek at what's being dragged while hovering (before release).
    public static unsafe bool IsDragging(string type)
    {
        var payload = ImGui.GetDragDropPayload();
        return payload.NativePtr != null && payload.IsDataType(type);
    }
}
