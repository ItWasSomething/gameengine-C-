using System.Numerics;
using ImGuiNET;

namespace Editor;

// MyEngine's look: black & white, hard edges — the same identity as the
// MyEngine Games website. Rules:
//   • No rounded corners anywhere (every *Rounding is 0).
//   • Grays only. The single exceptions carry meaning you'd lose in gray:
//     red for errors, amber for warnings, and the X/Y/Z axis colors on
//     vectors and gizmos (you need to see *which* axis you're dragging).
//   • White means "active": the focused panel's outline, the selected tab,
//     checkmarks, slider grabs, the Play button while playing.
//
// How panels work: docked panels get a transparent background, so the root
// window's black shows through as a 1px seam. BeginPanel then fills the
// panel's rectangle (inset by Gap) with the card color before any content.
public static class EditorTheme
{
    // ---- Palette -------------------------------------------------------------
    public static readonly Vector4 Bg0 = Rgb(0x000000);        // seams between panels, title strip
    public static readonly Vector4 Card = Rgb(0x0F0F0F);       // panel backgrounds
    public static readonly Vector4 Bg2 = Rgb(0x1C1C1C);        // widgets
    public static readonly Vector4 Bg3 = Rgb(0x2A2A2A);        // hovered widgets
    public static readonly Vector4 Border = Rgb(0x2E2E2E);

    public static readonly Vector4 Text = Rgb(0xF2F2F2);
    public static readonly Vector4 TextDim = Rgb(0x8C8C8C);
    public static readonly Vector4 Accent = Rgb(0xFFFFFF);     // "active"
    public static readonly Vector4 AccentDim = Rgb(0xBDBDBD);
    public static readonly Vector4 Amber = Rgb(0xD9D9D9);      // kept for API compatibility; it's gray now
    public static readonly Vector4 Success = Rgb(0xBFBFBF);
    public static readonly Vector4 Warning = Rgb(0xE8B04A);
    public static readonly Vector4 Error = Rgb(0xFF5C5C);

    // Axis colors, used everywhere a vector shows up (inspector fields,
    // gizmo handles, grid lines) so X/Y/Z always read the same.
    public static readonly Vector4 AxisX = Rgb(0xE5484D);
    public static readonly Vector4 AxisY = Rgb(0x5DBB46);
    public static readonly Vector4 AxisZ = Rgb(0x3E8EF7);

    public const float Gap = 1f;            // seam between panels
    public const float CardRounding = 0f;
    public const float StatusBarHeight = 26f;

    public static ImFontPtr BoldFont { get; set; }
    // Big bold text (hub title, sign-in codes). A real 32px font: scaling a
    // 17px one with SetWindowFontScale looks blurry.
    public static ImFontPtr HeadingFont { get; set; }

    public static Vector4 Rgb(uint hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    private static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    // A slow 0..1 "breathing" value. Only used for the "playing" indicator now —
    // a calm editor shouldn't pulse while you're just editing.
    public static float Pulse => 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 2.2f);

    public static void Apply()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 0;
        style.FrameRounding = 0;
        style.PopupRounding = 0;
        style.GrabRounding = 0;
        style.TabRounding = 0;
        style.ScrollbarRounding = 0;
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 1;          // every input gets a crisp 1px edge
        style.TabBorderSize = 0;
        style.WindowPadding = new Vector2(12, 10);
        style.FramePadding = new Vector2(8, 5);
        style.ItemSpacing = new Vector2(8, 7);
        style.ItemInnerSpacing = new Vector2(6, 4);
        style.CellPadding = new Vector2(6, 4);
        style.IndentSpacing = 16;
        style.ScrollbarSize = 10;
        style.GrabMinSize = 10;
        style.WindowMenuButtonPosition = ImGuiDir.None;
        style.SeparatorTextBorderSize = 1;
        style.DockingSeparatorSize = Gap;
        style.WindowTitleAlign = new Vector2(0, 0.5f);

        var c = style.Colors;
        void Set(ImGuiCol col, Vector4 v) => c[(int)col] = v;

        Set(ImGuiCol.Text, Text);
        Set(ImGuiCol.TextDisabled, TextDim);
        Set(ImGuiCol.WindowBg, Card);                      // floating windows; docked panels are also painted by BeginPanel
        Set(ImGuiCol.ChildBg, Vector4.Zero);
        Set(ImGuiCol.PopupBg, Rgb(0x141414, 0.99f));
        Set(ImGuiCol.Border, Border);
        Set(ImGuiCol.BorderShadow, Vector4.Zero);
        Set(ImGuiCol.FrameBg, Bg2);
        Set(ImGuiCol.FrameBgHovered, Bg3);
        Set(ImGuiCol.FrameBgActive, Rgb(0x333333));
        Set(ImGuiCol.TitleBg, Bg0);
        Set(ImGuiCol.TitleBgActive, Bg2);
        Set(ImGuiCol.TitleBgCollapsed, Bg0);
        Set(ImGuiCol.MenuBarBg, Vector4.Zero);              // the title strip draws its own background
        Set(ImGuiCol.ScrollbarBg, Vector4.Zero);
        Set(ImGuiCol.ScrollbarGrab, Bg3);
        Set(ImGuiCol.ScrollbarGrabHovered, Rgb(0x4A4A4A));
        Set(ImGuiCol.ScrollbarGrabActive, AccentDim);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, AccentDim);
        Set(ImGuiCol.SliderGrabActive, Accent);
        Set(ImGuiCol.Button, Bg2);
        Set(ImGuiCol.ButtonHovered, Bg3);
        Set(ImGuiCol.ButtonActive, WithAlpha(Accent, 0.30f));
        Set(ImGuiCol.Header, WithAlpha(Accent, 0.14f));
        Set(ImGuiCol.HeaderHovered, WithAlpha(Accent, 0.08f));
        Set(ImGuiCol.HeaderActive, WithAlpha(Accent, 0.20f));
        Set(ImGuiCol.Separator, Border);
        Set(ImGuiCol.SeparatorHovered, AccentDim);
        Set(ImGuiCol.SeparatorActive, Accent);
        Set(ImGuiCol.ResizeGrip, Vector4.Zero);
        Set(ImGuiCol.ResizeGripHovered, AccentDim);
        Set(ImGuiCol.ResizeGripActive, Accent);
        // Unselected tabs sit on the black strip; the selected one is lifted to
        // a lighter gray so it reads as "the open page".
        Set(ImGuiCol.Tab, Vector4.Zero);
        Set(ImGuiCol.TabHovered, Bg3);
        Set(ImGuiCol.TabActive, Bg3);
        Set(ImGuiCol.TabUnfocused, Vector4.Zero);
        Set(ImGuiCol.TabUnfocusedActive, Rgb(0x222222));
        Set(ImGuiCol.DockingPreview, WithAlpha(Accent, 0.35f));
        Set(ImGuiCol.DockingEmptyBg, Bg0);
        Set(ImGuiCol.TextSelectedBg, WithAlpha(Accent, 0.28f));
        Set(ImGuiCol.DragDropTarget, Accent);
        Set(ImGuiCol.NavHighlight, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);
        Set(ImGuiCol.PlotLines, Accent);
        Set(ImGuiCol.TableHeaderBg, Bg2);
        Set(ImGuiCol.TableBorderLight, Border);
        Set(ImGuiCol.TableBorderStrong, Border);
        Set(ImGuiCol.ModalWindowDimBg, new Vector4(0, 0, 0, 0.7f));
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

    // The card rectangle of the current panel (inside the seams, below the tabs).
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
        dl.AddRectFilled(min, max, U32(Card));
        bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        dl.AddRect(min, max, U32(focused ? WithAlpha(Accent, 0.55f) : Border));
        dl.PopClipRect();
    }

    // For panels whose content covers their card (the Scene and Game views):
    // draw the focus outline on top, around the image.
    public static void DrawFocusOutline(Vector2 min, Vector2 max)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        dl.PushClipRect(pos, pos + ImGui.GetWindowSize(), false);
        dl.AddRect(min, max, U32(WithAlpha(Accent, 0.55f)));
        dl.PopClipRect();
    }

    // ---- Little pieces ------------------------------------------------------------------

    // A small square status marker, vertically centered on the text line.
    public static void StatusDot(Vector4 color, float radius = 3.5f)
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        var c = new Vector2(p.X + radius + 1, p.Y + h / 2);
        ImGui.GetWindowDrawList().AddRectFilled(c - new Vector2(radius), c + new Vector2(radius), U32(color));
        ImGui.Dummy(new Vector2(radius * 2 + 2, h));
    }

    // The logo in the title strip: the same square-in-square mark as the
    // website, then the name.
    public static void DrawLogo()
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        var c = new Vector2(p.X + 10, p.Y + h / 2);
        var dl = ImGui.GetWindowDrawList();
        const float r = 7f;
        dl.AddRectFilled(c - new Vector2(r), c + new Vector2(r), U32(Accent));
        dl.AddRectFilled(c - new Vector2(r * 0.4f), c + new Vector2(r * 0.4f), U32(Bg0));
        ImGui.Dummy(new Vector2(22, h));
        ImGui.SameLine(0, 6);
        ImGui.PushFont(BoldFont);
        ImGui.TextColored(Text, "MYENGINE");
        ImGui.PopFont();
    }

    // Background of the title strip: flat black with a 1px line along the
    // bottom. While playing, the line becomes a white bar so you can't
    // mistake play mode for edit mode (changes made while playing are lost).
    public static void DrawTitleStrip(Vector2 min, Vector2 max, bool playing = false)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, U32(Bg0));
        if (playing)
            dl.AddRectFilled(new Vector2(min.X, max.Y - 2), max, U32(WithAlpha(Accent, 0.7f + 0.3f * Pulse)));
        else
            dl.AddLine(new Vector2(min.X, max.Y - 0.5f), new Vector2(max.X, max.Y - 0.5f), U32(Border));
    }

    // A full-width primary button: white with black text — the editor's
    // equivalent of the website's black "primary" buttons.
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0xDDDDDD));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0xBBBBBB));
        ImGui.PushStyleColor(ImGuiCol.Text, Rgb(0x000000));
        ImGui.PushFont(BoldFont);
        bool pressed = ImGui.Button(label, size);
        ImGui.PopFont();
        ImGui.PopStyleColor(4);
        return pressed;
    }

    // A square toggle used instead of ImGui's round radio buttons: white when
    // selected, gray otherwise. Returns true when clicked.
    public static bool ToggleButton(string label, bool selected, Vector2 size = default)
    {
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Accent);
            ImGui.PushStyleColor(ImGuiCol.Text, Rgb(0x000000));
        }
        bool pressed = ImGui.Button(label, size);
        if (selected) ImGui.PopStyleColor(4);
        return pressed;
    }

    // Small uppercase gray caption, like the website's ".label".
    public static void Label(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
    }

    // What an empty panel shows: a bold title and a gray hint, centered.
    public static void EmptyState(string title, string hint)
    {
        var avail = ImGui.GetContentRegionAvail();
        var titleSize = ImGui.CalcTextSize(title);
        float wrap = MathF.Max(120, avail.X - 24);
        var hintSize = ImGui.CalcTextSize(hint, wrap);
        float y = MathF.Max(0, (avail.Y - titleSize.Y - hintSize.Y - 6) * 0.4f);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + y);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (avail.X - titleSize.X) / 2));
        ImGui.PushFont(BoldFont);
        ImGui.TextUnformatted(title);
        ImGui.PopFont();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(12, (avail.X - MathF.Min(hintSize.X, wrap)) / 2));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
        ImGui.TextColored(TextDim, hint);
        ImGui.PopTextWrapPos();
    }
}
