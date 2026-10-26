using Engine;
using ImGuiNET;

namespace Editor.Panels;

// Unity's Console: everything sent through Engine.Log (and Debug.Log). Repeated identical messages
// (say, an error thrown every frame) collapse into one line with a count,
// so one broken script can't bury everything else.
public sealed class ConsolePanel
{
    public const string Title = "CONSOLE###Console";
    private const int MaxEntries = 1000;

    private readonly List<(LogEntry Entry, int Count)> _entries = new();
    private readonly object _lock = new();
    private bool _scrollToBottom;
    private bool _showInfo = true, _showWarnings = true, _showErrors = true;

    // Like Unity: wipe old messages when Play starts, so what you see is
    // from this run.
    public bool ClearOnPlay = true;

    // Opens a script at a line; set by EditorApp. `file` is project-relative
    // (compile errors) or just a file name (runtime exceptions).
    public Action<string, int>? OpenLocation;

    private static readonly System.Text.RegularExpressions.Regex CompilerFormat =
        new(@"^(?<file>[^()]+\.cs)\((?<line>\d+),\d+\)");
    private static readonly System.Text.RegularExpressions.Regex StackFormat =
        new(@"\((?<file>[^():]+\.cs):(?<line>\d+)\)$");

    private static (string File, int Line)? ParseLocation(string message)
    {
        var m = CompilerFormat.Match(message);
        if (!m.Success) m = StackFormat.Match(message);
        return m.Success ? (m.Groups["file"].Value, int.Parse(m.Groups["line"].Value)) : null;
    }

    public void Clear() { lock (_lock) _entries.Clear(); }

    public ConsolePanel()
    {
        Log.MessageLogged += OnMessage;
    }

    private void OnMessage(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count > 0 && _entries[^1].Entry.Message == entry.Message && _entries[^1].Entry.Level == entry.Level)
                _entries[^1] = (entry, _entries[^1].Count + 1);
            else
                _entries.Add((entry, 1));
            if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }
        _scrollToBottom = true;
    }

    public void Draw(ImFontPtr monoFont)
    {
        if (!EditorTheme.BeginPanel(Title)) { ImGui.End(); return; }

        if (ImGui.SmallButton("Clear")) Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Clear on Play", ref ClearOnPlay);
        ImGui.SameLine();
        ImGui.Checkbox("Info", ref _showInfo);
        ImGui.SameLine();
        ImGui.Checkbox("Warnings", ref _showWarnings);
        ImGui.SameLine();
        ImGui.Checkbox("Errors", ref _showErrors);
        ImGui.Separator();

        ImGui.BeginChild("log");
        ImGui.PushFont(monoFont);
        lock (_lock)
        {
            foreach (var (entry, count) in _entries)
            {
                if (entry.Level == LogLevel.Info && !_showInfo) continue;
                if (entry.Level == LogLevel.Warning && !_showWarnings) continue;
                if (entry.Level == LogLevel.Error && !_showErrors) continue;

                var color = entry.Level switch
                {
                    LogLevel.Warning => EditorTheme.Warning,
                    LogLevel.Error => EditorTheme.Error,
                    _ => EditorTheme.Text,
                };
                ImGui.TextColored(EditorTheme.TextDim, entry.Time.ToString("HH:mm:ss"));
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                string text = count > 1 ? $"{entry.Message}  (x{count})" : entry.Message;
                // "Assets/Scripts/Foo.cs(12,5): error ..." or "(Foo.cs:12)" —
                // clickable, opens the script at that line.
                var location = ParseLocation(entry.Message);
                if (location != null && OpenLocation != null)
                {
                    if (ImGui.Selectable(text + "##" + entry.Time.Ticks))
                        OpenLocation(location.Value.File, location.Value.Line);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to open in the Script Editor");
                }
                else ImGui.TextWrapped(text);
                ImGui.PopStyleColor();
            }
        }
        ImGui.PopFont();
        if (_scrollToBottom && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40)
            ImGui.SetScrollHereY(1f);
        _scrollToBottom = false;
        ImGui.EndChild();

        ImGui.End();
    }
}
