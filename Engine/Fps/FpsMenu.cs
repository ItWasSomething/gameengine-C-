using System.Numerics;
using Silk.NET.Maths;

namespace Engine.Fps;

// What the window should do after the player picks a menu item. Moving
// between menu pages (Controls, Back) is handled inside FpsMenu and never
// reaches the window — only things that affect the game itself do.
public enum MenuCommand { None, Play, Resume, Restart, MainMenu, Quit }

// The title screen and the pause screen. Like FpsWorld it knows nothing
// about Silk.NET input: FpsWindow feeds it "move up", "confirm", "mouse is
// here" and gets a MenuCommand back. It does draw itself, since a menu is
// mostly layout and the layout is needed for both drawing and mouse hits.
public sealed class FpsMenu
{
    public enum Page { Main, Pause, Controls }

    // Rename freely — it's only ever shown on the title screen.
    public const string GameTitle = "WAVE ARENA";

    private static readonly (string Label, MenuCommand Command)[] MainItems =
    {
        ("PLAY", MenuCommand.Play),
        ("CONTROLS", MenuCommand.None),
        ("QUIT", MenuCommand.Quit),
    };

    private static readonly (string Label, MenuCommand Command)[] PauseItems =
    {
        ("RESUME", MenuCommand.Resume),
        ("RESTART", MenuCommand.Restart),
        ("CONTROLS", MenuCommand.None),
        ("MAIN MENU", MenuCommand.MainMenu),
        ("QUIT", MenuCommand.Quit),
    };

    private static readonly (string Label, MenuCommand Command)[] ControlsItems =
    {
        ("BACK", MenuCommand.None),
    };

    private static readonly (string Key, string Action)[] ControlLines =
    {
        ("MOUSE", "LOOK"),
        ("W A S D", "MOVE"),
        ("SPACE", "JUMP / WALL-JUMP"),
        ("HOLD W BY A WALL", "WALL-RUN (IN THE AIR)"),
        ("SHIFT", "SPRINT"),
        ("LEFT CLICK", "SHOOT"),
        ("1-4 / WHEEL", "SWITCH WEAPON"),
        ("R", "RELOAD"),
        ("ESC", "PAUSE"),
    };

    private static readonly Vector4 Gold = new(1f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.75f, 0.75f, 0.8f, 1f);
    private static readonly Vector4 Faint = new(0.55f, 0.55f, 0.6f, 1f);

    public Page CurrentPage { get; private set; } = Page.Main;
    public int Selected { get; private set; }

    // One line under the title / pause heading, set by the window
    // ("BEST: WAVE 4 - 23 KILLS", "WAVE 2 - 9 KILLS"). Empty = not shown.
    public string StatusLine = "";

    private Page _controlsReturnPage = Page.Main;
    private float _time; // drives the little pulse/bob animations

    private (string Label, MenuCommand Command)[] Items => CurrentPage switch
    {
        Page.Pause => PauseItems,
        Page.Controls => ControlsItems,
        _ => MainItems,
    };

    public void Open(Page page)
    {
        CurrentPage = page;
        Selected = 0;
    }

    public void Update(float dt) => _time += dt;

    // ===================================================================== input

    // Wraps around, so pressing Up on the first item lands on the last.
    public void MoveSelection(int delta)
    {
        int count = Items.Length;
        Selected = ((Selected + delta) % count + count) % count;
    }

    public MenuCommand Confirm()
    {
        var (label, command) = Items[Selected];
        if (command != MenuCommand.None) return command;

        // Page-to-page navigation stays inside the menu.
        if (label == "CONTROLS")
        {
            _controlsReturnPage = CurrentPage;
            Open(Page.Controls);
        }
        else if (label == "BACK")
        {
            ReturnFromControls();
        }
        return MenuCommand.None;
    }

    // Esc: back out of Controls, or close the pause menu. On the title
    // screen it does nothing — quitting should be a deliberate choice.
    public MenuCommand Back()
    {
        switch (CurrentPage)
        {
            case Page.Controls:
                ReturnFromControls();
                return MenuCommand.None;
            case Page.Pause:
                return MenuCommand.Resume;
            default:
                return MenuCommand.None;
        }
    }

    private void ReturnFromControls()
    {
        Open(_controlsReturnPage);
        // Land back on the CONTROLS item rather than the top of the list.
        int index = Array.FindIndex(Items, item => item.Label == "CONTROLS");
        Selected = Math.Max(0, index);
    }

    // Mouse positions are in HUD space: pixels, origin bottom-left.
    public void Hover(Vector2 mouse, Vector2 screen)
    {
        int index = ItemAt(mouse, screen);
        if (index >= 0) Selected = index;
    }

    public MenuCommand Click(Vector2 mouse, Vector2 screen)
    {
        int index = ItemAt(mouse, screen);
        if (index < 0) return MenuCommand.None;
        Selected = index;
        return Confirm();
    }

    private int ItemAt(Vector2 mouse, Vector2 screen)
    {
        for (int i = 0; i < Items.Length; i++)
        {
            var (center, size) = ItemRect(i, screen);
            if (MathF.Abs(mouse.X - center.X) <= size.X / 2f && MathF.Abs(mouse.Y - center.Y) <= size.Y / 2f)
                return i;
        }
        return -1;
    }

    // ===================================================================== layout

    // Everything is sized off one "unit" that grows with the window height,
    // kept a whole number so the pixel font stays crisp: 720p -> 2, 1080p -> 3.
    private static int Unit(Vector2 screen) => Math.Max(1, (int)(screen.Y / 360f));

    // Item rectangles, centre + size, in HUD pixels. Used both to draw the
    // highlight bar and to test whether the mouse is over an item, so the
    // two can never disagree.
    private (Vector2 Center, Vector2 Size) ItemRect(int index, Vector2 screen)
    {
        int unit = Unit(screen);
        float spacing = 32f * unit;
        float firstY = CurrentPage switch
        {
            Page.Main => screen.Y * 0.46f,
            Page.Pause => screen.Y * 0.58f,
            _ => screen.Y * 0.14f,
        };
        var center = new Vector2(screen.X / 2f, firstY - index * spacing);
        return (center, new Vector2(170f * unit, 26f * unit));
    }

    // ===================================================================== draw

    public void Draw(TextRenderer text, Sprite white, Matrix4x4 projection, Vector2 screen)
    {
        int unit = Unit(screen);
        float cx = screen.X / 2f;

        // Darken whatever is behind (the slowly turning arena on the title
        // screen, the frozen game when paused) so the text reads clearly.
        float darkness = CurrentPage == Page.Main ? 0.35f : 0.6f;
        white.Draw(new(cx, screen.Y / 2f), new(screen.X, screen.Y), projection, new Vector4(0f, 0f, 0.02f, darkness));

        switch (CurrentPage)
        {
            case Page.Main:
                DrawTitle(text, projection, screen, unit);
                break;
            case Page.Pause:
                text.Draw("PAUSED", new(cx, screen.Y * 0.74f), 6 * unit, Gold, projection, TextAlign.Center);
                if (StatusLine.Length > 0)
                    text.Draw(StatusLine, new(cx, screen.Y * 0.74f - 16f * unit), 2 * unit, Dim, projection, TextAlign.Center);
                break;
            case Page.Controls:
                DrawControls(text, projection, screen, unit);
                break;
        }

        DrawItems(text, white, projection, screen, unit);

        text.Draw("W/S OR MOUSE TO PICK - ENTER OR CLICK TO CHOOSE", new(cx, 12f * unit), unit, Faint, projection,
            TextAlign.Center);
    }

    private void DrawTitle(TextRenderer text, Matrix4x4 projection, Vector2 screen, int unit)
    {
        float cx = screen.X / 2f;
        float titleY = screen.Y * 0.68f;

        // A slow "breathing" glow: the colour drifts between gold and a
        // brighter gold. Values above 1 are clamped to white by the GPU.
        float pulse = 0.5f + 0.5f * MathF.Sin(_time * 2f);
        var titleColor = Vector4.Lerp(Gold, new Vector4(1.2f, 1f, 0.6f, 1f), pulse * 0.5f);
        text.Draw(GameTitle, new(cx, titleY), 7 * unit, titleColor, projection, TextAlign.Center);
        text.Draw("SURVIVE THE WAVES", new(cx, titleY - 22f * unit), 2 * unit, Dim, projection, TextAlign.Center);

        if (StatusLine.Length > 0)
            text.Draw(StatusLine, new(cx, 30f * unit), unit * 2, Faint, projection, TextAlign.Center);
    }

    private static void DrawControls(TextRenderer text, Matrix4x4 projection, Vector2 screen, int unit)
    {
        float cx = screen.X / 2f;
        text.Draw("CONTROLS", new(cx, screen.Y * 0.82f), 6 * unit, Gold, projection, TextAlign.Center);

        // Two columns meeting at the centre: keys right-aligned, actions
        // left-aligned, so the gap between them lines up down the page.
        float y = screen.Y * 0.68f;
        float lineSpacing = 14f * unit;
        foreach (var (key, action) in ControlLines)
        {
            text.Draw(key, new(cx - 8f * unit, y), 2 * unit, Gold, projection, TextAlign.Right);
            text.Draw(action, new(cx + 8f * unit, y), 2 * unit, Dim, projection);
            y -= lineSpacing;
        }
    }

    private void DrawItems(TextRenderer text, Sprite white, Matrix4x4 projection, Vector2 screen, int unit)
    {
        var items = Items;
        float scale = 3 * unit;
        float textHeight = TextRenderer.LineHeight(scale);

        for (int i = 0; i < items.Length; i++)
        {
            var (center, size) = ItemRect(i, screen);
            bool selected = i == Selected;
            // Text is drawn from its baseline, so drop it by half its height
            // to centre it vertically in the item's rectangle.
            var baseline = new Vector2(center.X, center.Y - textHeight / 2f);

            if (selected)
            {
                white.Draw(new(center.X, center.Y), new(size.X, size.Y), projection, new Vector4(1f, 0.78f, 0.35f, 0.14f));

                // Arrows either side of the label, nudging in and out.
                float half = TextRenderer.Measure(items[i].Label, scale) / 2f;
                float bob = (2f + MathF.Sin(_time * 6f) * 2f) * unit;
                text.Draw(">", new(center.X - half - 10f * unit - bob, baseline.Y), scale, Gold, projection, TextAlign.Right);
                text.Draw("<", new(center.X + half + 10f * unit + bob, baseline.Y), scale, Gold, projection);
            }

            text.Draw(items[i].Label, baseline, scale, selected ? Gold : Dim, projection, TextAlign.Center);
        }
    }
}
