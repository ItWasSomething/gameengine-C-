using System.Numerics;
using ImGuiNET;

namespace Editor;

// MyEngine's look: "warm forge". A deep warm charcoal, an ember-orange
// accent (the platformer player's orange), panels drawn as rounded cards
// with small gaps between them, and a soft breathing glow around whichever
// panel has focus — so the editor has its own identity instead of looking
// like stock ImGui (or a Unity/Godot clone).
//
// How the cards work: docked panels get a transparent background, so the
// root window's darkest color shows through as the "gap". BeginPanel then
// paints a slightly lighter rounded rectangle, inset by Gap pixels, as the
// panel's real background — before any content, so content draws on top.
public static class EditorTheme
{
    // ---- Palette -------------------------------------------------------------
    public static readonly Vector4 Bg0 = Rgb(0x0F0C0A);        // gaps between cards, title strip
    public static readonly Vector4 Card = Rgb(0x1A1512);       // panel backgrounds
    public static readonly Vector4 Bg2 = Rgb(0x26201B);        // widgets
    public static readonly Vector4 Bg3 = Rgb(0x332A23);        // hovered widgets
    public static readonly Vector4 Border = Rgb(0x2E2620);

    public static readonly Vector4 Text = Rgb(0xEDE6DF);
    public static readonly Vector4 TextDim = Rgb(0x8F837A);
    public static readonly Vector4 Accent = Rgb(0xFF7A2F);     // ember
    public static readonly Vector4 AccentDim = Rgb(0xB4541F);
    public static readonly Vector4 Amber = Rgb(0xFFB347);
    public static readonly Vector4 Success = Rgb(0x8FD18F);
    public static readonly Vector4 Warning = Rgb(0xF2C14E);
    public static readonly Vector4 Error = Rgb(0xF2545B);

    // Axis colors, used everywhere a vector shows up (inspector fields,
    // gizmo handles, grid lines) so X/Y/Z always read the same.
    public static readonly Vector4 AxisX = Rgb(0xE5484D);
    public static readonly Vector4 AxisY = Rgb(0x6BCB3F);
    public static readonly Vector4 AxisZ = Rgb(0x3E8EF7);

    public const float Gap = 5f;            // space between cards
    public const float CardRounding = 8f;
    public const float StatusBarHeight = 26f;

    public static ImFontPtr BoldFont { get; set; }

    public static Vector4 Rgb(uint hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    private static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    // A slow 0..1 "breathing" value shared by every glow, so they pulse together.
    public static float Pulse => 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 2.2f);

    public static void Apply()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = CardRounding;
        style.ChildRounding = 6;
        style.FrameRounding = 6;
        style.PopupRounding = 8;
        style.GrabRounding = 6;
        style.TabRounding = 7;
        style.ScrollbarRounding = 8;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 0;
        style.TabBorderSize = 0;
        style.WindowPadding = new Vector2(12, 10);
        style.FramePadding = new Vector2(8, 4);
        style.ItemSpacing = new Vector2(8, 6);
        style.ItemInnerSpacing = new Vector2(6, 4);
        style.IndentSpacing = 16;
        style.ScrollbarSize = 11;
        style.GrabMinSize = 10;
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.SeparatorTextBorderSize = 1;
        style.DockingSeparatorSize = Gap;

        var c = style.Colors;
        void Set(ImGuiCol col, Vector4 v) => c[(int)col] = v;

        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, TextDim);
        Set(ImGuiCol.WindowBg, Vector4.Zero);              // cards are painted by BeginPanel
        Set(ImGuiCol.ChildBg, Vector4.Zero);
        Set(ImGuiCol.PopupBg, WithAlpha(Rgb(0x1E1814), 0.98f));
        Set(ImGuiCol.Border, Border);
        Set(ImGuiCol.BorderShadow, Vector4.Zero);
        Set(ImGuiCol.FrameBg, Bg2);
        Set(ImGuiCol.FrameBgHovered, Bg3);
        Set(ImGuiCol.FrameBgActive, Rgb(0x3D3229));
        Set(ImGuiCol.TitleBg, Bg0);
        Set(ImGuiCol.TitleBgActive, Bg2);
        Set(ImGuiCol.TitleBgCollapsed, Bg0);
        Set(ImGuiCol.MenuBarBg, Vector4.Zero);              // the title strip draws its own gradient
        Set(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Set(ImGuiCol.ScrollbarGrab, Bg3);
        Set(ImGuiCol.ScrollbarGrabHovered, Rgb(0x4A3D33));
        Set(ImGuiCol.ScrollbarGrabActive, AccentDim);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, AccentDim);
        Set(ImGuiCol.SliderGrabActive, Accent);
        Set(ImGuiCol.Button, Bg2);
        Set(ImGuiCol.ButtonHovered, Rgb(0x3A2C22));
        Set(ImGuiCol.ButtonActive, AccentDim);
        Set(ImGuiCol.Header, WithAlpha(Accent, 0.20f));
        Set(ImGuiCol.HeaderHovered, WithAlpha(Accent, 0.12f));
        Set(ImGuiCol.HeaderActive, WithAlpha(Accent, 0.30f));
        Set(ImGuiCol.Separator, Border);
        Set(ImGuiCol.SeparatorHovered, AccentDim);
        Set(ImGuiCol.SeparatorActive, Accent);
        Set(ImGuiCol.ResizeGrip, Vector4.Zero);
        Set(ImGuiCol.ResizeGripHovered, AccentDim);
        Set(ImGuiCol.ResizeGripActive, Accent);
        // The selected tab is the same color as its card, so tab + card read
        // as one shape — like a folder tab.
        Set(ImGuiCol.Tab, Bg0);
        Set(ImGuiCol.TabHovered, Bg3);
        Set(ImGuiCol.TabActive, Card);
        Set(ImGuiCol.TabUnfocused, Bg0);
        Set(ImGuiCol.TabUnfocusedActive, Card);
        Set(ImGuiCol.DockingPreview, WithAlpha(Accent, 0.45f));
        Set(ImGuiCol.DockingEmptyBg, Bg0);
        Set(ImGuiCol.TextSelectedBg, WithAlpha(Accent, 0.35f));
        Set(ImGuiCol.DragDropTarget, Amber);
        Set(ImGuiCol.NavHighlight, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotLines, Accent);
        Set(ImGuiCol.TableHeaderBg, Bg2);
        Set(ImGuiCol.TableBorderLight, Border);
        Set(ImGuiCol.TableBorderStrong, Border);
        Set(ImGuiCol.ModalWindowDimBg, new Vector4(0.03f, 0.02f, 0.01f, 0.6f));
    }

    // ---- Panels -------------------------------------------------------------------

    public static bool BeginPanel(string title, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        bool visible = ImGui.Begin(title, flags);
        if (visible) DrawCard();
        return visible;
    }

    public static bool BeginPanel(string title, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        bool visible = ImGui.Begin(title, ref open, flags);
        if (visible) DrawCard();
        return visible;
    }

    // The card rectangle of the current panel (inside the gaps, below the tabs).
    public static (Vector2 Min, Vector2 Max) CardRect()
    {
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        bool docked = ImGui.IsWindowDocked();
        float gap = docked ? Gap : 0;
        float top = pos.Y + ImGui.GetWindowContentRegionMin().Y - ImGui.GetStyle().WindowPadding.Y;
        return (new Vector2(pos.X + gap, top), pos + size - new Vector2(gap, gap));
    }

    private static void DrawCard()
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var (min, max) = CardRect();
        dl.PushClipRect(pos, pos + ImGui.GetWindowSize(), false);
        var corners = ImGui.IsWindowDocked() ? ImDrawFlags.RoundCornersBottom | ImDrawFlags.RoundCornersTopRight : ImDrawFlags.RoundCornersAll;
        dl.AddRectFilled(min, max, U32(Card), CardRounding, corners);
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) DrawGlow(dl, min, max, corners);
        else dl.AddRect(min, max, U32(Border), CardRounding, corners);
        dl.PopClipRect();
    }

    // Soft ember glow: a few widening, fading outlines around the rectangle,
    // gently pulsing.
    private static void DrawGlow(ImDrawListPtr dl, Vector2 min, Vector2 max, ImDrawFlags corners)
    {
        float strength = 0.75f + 0.25f * Pulse;
        for (int i = 4; i >= 1; i--)
        {
            float a = 0.07f * (5 - i) * strength;
            dl.AddRect(min - new Vector2(i), max + new Vector2(i), U32(WithAlpha(Accent, a)), CardRounding + i, corners, 1.5f);
        }
        dl.AddRect(min, max, U32(WithAlpha(Accent, 0.55f * strength)), CardRounding, corners, 1.2f);
    }

    // For panels whose content covers their card (the Scene and Game views):
    // draw the focus glow on top, around the image.
    public static void DrawFocusOutline(Vector2 min, Vector2 max)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        dl.PushClipRect(pos, pos + ImGui.GetWindowSize(), false);
        DrawGlow(dl, min, max, ImDrawFlags.RoundCornersBottom);
        dl.PopClipRect();
    }

    // ---- Little pieces ------------------------------------------------------------------

    // A small colored dot, vertically centered on the text line.
    public static void StatusDot(Vector4 color, float radius = 3.5f)
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        var c = new Vector2(p.X + radius + 1, p.Y + h / 2);
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(c, radius + 2, U32(WithAlpha(color, 0.18f)));
        dl.AddCircleFilled(c, radius, U32(color));
        ImGui.Dummy(new Vector2(radius * 2 + 2, h));
    }

    // The logo in the title strip: a glowing ember diamond and the name.
    public static void DrawLogo()
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        var c = new Vector2(p.X + 10, p.Y + h / 2);
        var dl = ImGui.GetWindowDrawList();
        float pulse = Pulse;
        for (int i = 3; i >= 1; i--)
            dl.AddCircleFilled(c, 5 + i * 3 * (0.8f + 0.2f * pulse), U32(WithAlpha(Accent, 0.05f * (4 - i) * (0.7f + 0.3f * pulse))));
        float r = 6.5f;
        dl.AddQuadFilled(c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), U32(Accent));
        dl.AddQuadFilled(c + new Vector2(0, -r * 0.5f), c + new Vector2(r * 0.5f, 0), c + new Vector2(0, r * 0.5f), c + new Vector2(-r * 0.5f, 0), U32(Amber));
        ImGui.Dummy(new Vector2(22, h));
        ImGui.SameLine(0, 4);
        ImGui.PushFont(BoldFont);
        ImGui.TextColored(Text, "MYENGINE");
        ImGui.PopFont();
    }

    // Background of the title strip: a warm gradient fading to the right,
    // with a thin ember line along the bottom edge.
    public static void DrawTitleStrip(Vector2 min, Vector2 max)
    {
        var dl = ImGui.GetWindowDrawList();
        uint left = U32(Rgb(0x2A1C14)), right = U32(Bg0);
        dl.AddRectFilledMultiColor(min, max, left, right, right, left);
        uint lineLeft = U32(WithAlpha(Accent, 0.8f)), lineRight = U32(WithAlpha(Accent, 0f));
        dl.AddRectFilledMultiColor(new Vector2(min.X, max.Y - 1.5f), new Vector2(min.X + (max.X - min.X) * 0.6f, max.Y), lineLeft, lineRight, lineRight, lineLeft);
    }
}
