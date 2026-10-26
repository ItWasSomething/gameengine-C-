using System.Numerics;
using Editor.Code;
using Editor.Scripting;
using Engine;
using Engine.Core;
using ImGuiNET;

namespace Editor.Panels;

// The built-in script editor: tabs of CodeEditor widgets with syntax
// highlighting, live errors (checked as you type, before saving),
// autocomplete from the real compiler, find/replace and the usual editing
// shortcuts. Ctrl+S saves, which triggers recompile + hot-reload.
public sealed class ScriptEditorPanel
{
    public const string Title = "SCRIPT EDITOR###Script Editor";

    private sealed class Document
    {
        public required string Path;
        public required CodeEditor Editor;
        public string SavedText = "";
        public DateTime DiskTime;
        public int CheckedVersion = -1;
        public int SavedVersion;
        public DateTime LastEdit;
        public int LastSeenVersion;
        public Task<List<ScriptDiagnostic>>? CheckTask;
        public List<ScriptDiagnostic>? LiveDiagnostics;
        public bool Dirty => Editor.Version != SavedVersion && Editor.Text != SavedText;
    }

    private readonly EditorContext _ctx;
    private readonly ScriptSystem _scripts;
    private readonly List<Document> _docs = new();
    private Document? _selectRequest;
    private Document? _active;
    private Document? _confirmClose;
    private bool _visible;
    private bool _focusRequest;
    private string _newScriptName = "NewBehaviour";
    private DateTime _lastDiskCheck;
    private HashSet<string> _knownTypes = new();
    private int _knownTypesGeneration = -1;

    public ScriptEditorPanel(EditorContext ctx, ScriptSystem scripts)
    {
        _ctx = ctx;
        _scripts = scripts;
        // Roslyn's first analysis takes ~1 s (loading + JIT); do it now in the
        // background so the first autocomplete popup appears instantly.
        string assets = AssetDatabase.assetsFolder;
        Task.Run(() =>
        {
            try { ScriptAnalysis.Complete(assets, Path.Combine(assets, "__warmup.cs"), "class W : MonoBehaviour { void F() { transform. } }", 49, out _); }
            catch { /* only a warm-up */ }
        });
    }

    public void Open(string path, int? line = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var doc = _docs.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (doc == null)
        {
            if (!File.Exists(path)) { Log.Error($"Script not found: {path}"); return; }
            var editor = new CodeEditor();
            editor.SetText(File.ReadAllText(path));
            string docPath = path;
            editor.CompletionProvider = (text, offset) =>
            {
                var items = ScriptAnalysis.Complete(AssetDatabase.assetsFolder, docPath, text, offset, out int start);
                return (items, start);
            };
            doc = new Document { Path = path, Editor = editor, SavedText = editor.Text, DiskTime = File.GetLastWriteTimeUtc(path) };
            doc.SavedVersion = editor.Version;
            _docs.Add(doc);
        }
        if (line is { } l) doc.Editor.GoToLine(l);
        doc.Editor.Focus();
        _selectRequest = doc;
        _visible = true;
        _focusRequest = true;
    }

    public void Show() => _visible = true;

    public void Draw(ImFontPtr monoFont)
    {
        if (!_visible) return;
        // Keep requesting focus until the tab really is in front (on the
        // first frames the dock layout can still override it).
        if (_focusRequest) ImGui.SetNextWindowFocus();
        bool open = EditorTheme.BeginPanel(Title, ref _visible);
        if (!open) { ImGui.End(); return; }
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) _focusRequest = false;

        RefreshKnownTypes();
        CheckDiskChanges();
        DrawToolbar();

        if (_docs.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No script open. Double-click a .cs file in the Project panel,");
            ImGui.TextDisabled("use Add Component > New Script, or press New Script above.");
            ImGui.End();
            return;
        }

        _active = null;
        if (ImGui.BeginTabBar("docs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
        {
            foreach (var doc in _docs.ToList())
            {
                var flags = doc.Dirty ? ImGuiTabItemFlags.UnsavedDocument : ImGuiTabItemFlags.None;
                if (_selectRequest == doc) { flags |= ImGuiTabItemFlags.SetSelected; _selectRequest = null; }
                bool tabOpen = true;
                if (ImGui.BeginTabItem(System.IO.Path.GetFileName(doc.Path) + "##" + doc.Path, ref tabOpen, flags))
                {
                    _active = doc;
                    DrawDocument(doc, monoFont);
                    ImGui.EndTabItem();
                }
                if (!tabOpen)
                {
                    if (doc.Dirty) _confirmClose = doc;
                    else _docs.Remove(doc);
                }
            }
            ImGui.EndTabBar();
        }

        // Ctrl+S saves while this panel (or the code in it) has focus.
        if (_active != null && (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || CodeEditor.AnyFocused) &&
            ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
            Save(_active);

        DrawCloseConfirm();
        ImGui.End();
    }

    // Names colored as types: engine components and math types, common .NET
    // types, and every class in your scripts.
    private void RefreshKnownTypes()
    {
        if (_knownTypesGeneration == _scripts.Generation) return;
        _knownTypesGeneration = _scripts.Generation;
        var set = new HashSet<string>
        {
            "Vector2", "Vector3", "Vector4", "Quaternion", "Matrix4x4", "Math", "MathF", "List", "Dictionary", "HashSet",
            "Queue", "Stack", "Action", "Func", "IEnumerator", "IEnumerable", "Random", "String", "Console", "Array",
            "Exception", "Key", "Input", "Debug", "Task", "DateTime", "TimeSpan", "Enum", "Guid",
        };
        foreach (var t in typeof(Component).Assembly.GetExportedTypes())
            if (t.Namespace == "Engine.Core") set.Add(t.Name.Split('`')[0]);
        foreach (var name in _scripts.TypeFiles.Keys) set.Add(name);
        _knownTypes = set;
        foreach (var doc in _docs) doc.Editor.KnownTypes = set;
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("+ New Script")) ImGui.OpenPopup("NewScript");
        if (ImGui.BeginPopup("NewScript"))
        {
            ImGui.TextDisabled("Class name (also the file name):");
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            bool enter = ImGui.InputText("##name", ref _newScriptName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            if (ImGui.Button("Create") || enter)
            {
                if (_scripts.CreateScript(_newScriptName, null) is { } file) Open(file);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        var current = _active ?? _docs.LastOrDefault();
        ImGui.BeginDisabled(current == null);
        if (ImGui.Button("Save") && current != null) Save(current);
        ImGui.SameLine();
        if (ImGui.Button("Open Externally") && current != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current.Path) { UseShellExecute = true });
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ctrl+S save & hot-reload   Ctrl+Space suggest   Ctrl+F find   Ctrl+H replace   Ctrl+G go to line\n" +
                             "Ctrl+/ comment   Ctrl+D duplicate line   Alt+Up/Down move line   Ctrl+Shift+K delete line\n" +
                             "Tab / Shift+Tab indent   Ctrl+Z / Ctrl+Y undo / redo   F3 next match");

        // Compile status, right-aligned.
        string status;
        Vector4 color;
        int errors = _scripts.Diagnostics.Count(d => d.IsError);
        if (_scripts.IsCompiling) { status = "Compiling..."; color = EditorTheme.Warning; }
        else if (errors > 0) { status = $"Last compile: {errors} error{(errors == 1 ? "" : "s")}"; color = EditorTheme.Error; }
        else { status = "Compiled"; color = EditorTheme.Success; }
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + 10, ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(status).X - 14));
        ImGui.AlignTextToFramePadding();
        EditorTheme.StatusDot(color);
        ImGui.SameLine(0, 6);
        ImGui.TextColored(color, status);
    }

    private void DrawDocument(Document doc, ImFontPtr monoFont)
    {
        UpdateLiveDiagnostics(doc);
        var diagnostics = doc.LiveDiagnostics ?? _scripts.Diagnostics
            .Where(d => string.Equals(System.IO.Path.GetFullPath(d.File), doc.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        doc.Editor.KnownTypes = _knownTypes;
        doc.Editor.Markers = diagnostics
            .Select(d => new EditorMarker(d.Line - 1, d.Column - 1, Math.Max(d.EndLine, d.Line) - 1, d.EndLine > 0 ? d.EndColumn - 1 : d.Column, d.Message, d.IsError))
            .ToList();

        int errorCount = diagnostics.Count(d => d.IsError), warningCount = diagnostics.Count - errorCount;
        float listHeight = diagnostics.Count > 0 ? MathF.Min(140, 30 + diagnostics.Count * ImGui.GetTextLineHeightWithSpacing()) : 0;
        float statusHeight = ImGui.GetFrameHeightWithSpacing();

        doc.Editor.Draw("##code" + doc.Path, new Vector2(-1, -(listHeight + statusHeight)), monoFont);

        // Status line: position, problems, saved state.
        var cursor = doc.Editor.Cursor;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"Ln {cursor.Line + 1}, Col {cursor.Col + 1}");
        ImGui.SameLine(0, 16);
        if (errorCount > 0) { EditorTheme.StatusDot(EditorTheme.Error); ImGui.SameLine(0, 4); ImGui.TextColored(EditorTheme.Error, $"{errorCount} error{(errorCount == 1 ? "" : "s")}"); ImGui.SameLine(0, 12); }
        if (warningCount > 0) { EditorTheme.StatusDot(EditorTheme.Warning); ImGui.SameLine(0, 4); ImGui.TextColored(EditorTheme.Warning, $"{warningCount} warning{(warningCount == 1 ? "" : "s")}"); ImGui.SameLine(0, 12); }
        if (diagnostics.Count == 0) { EditorTheme.StatusDot(EditorTheme.Success); ImGui.SameLine(0, 4); ImGui.TextColored(EditorTheme.Success, "No problems"); ImGui.SameLine(0, 12); }
        ImGui.TextDisabled(doc.Dirty ? "  modified  (Ctrl+S to save & hot-reload)" : "  saved");
        ImGui.SameLine();
        string assetPath = AssetDatabase.ToAssetPath(doc.Path);
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + 10, ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(assetPath).X - 8));
        ImGui.TextDisabled(assetPath);

        if (diagnostics.Count > 0)
        {
            ImGui.BeginChild("problems", new Vector2(-1, listHeight - 4), ImGuiChildFlags.Border);
            foreach (var d in diagnostics)
            {
                EditorTheme.StatusDot(d.IsError ? EditorTheme.Error : EditorTheme.Warning);
                ImGui.SameLine(0, 6);
                ImGui.PushStyleColor(ImGuiCol.Text, d.IsError ? EditorTheme.Error : EditorTheme.Warning);
                if (ImGui.Selectable($"Ln {d.Line}, Col {d.Column}   {d.Code}  {d.Message}##{d.Line}:{d.Column}:{d.Code}"))
                    doc.Editor.GoToLine(d.Line, d.Column);
                ImGui.PopStyleColor();
            }
            ImGui.EndChild();
        }
    }

    // Re-check the text you're typing ~0.4 s after you stop, on a
    // background thread, so squiggles appear without saving.
    private void UpdateLiveDiagnostics(Document doc)
    {
        if (doc.Editor.Version != doc.LastSeenVersion)
        {
            doc.LastSeenVersion = doc.Editor.Version;
            doc.LastEdit = DateTime.Now;
        }
        if (doc.CheckTask is { IsCompleted: true } done)
        {
            if (!done.IsFaulted) doc.LiveDiagnostics = done.Result;
            doc.CheckTask = null;
        }
        if (doc.CheckTask == null && doc.CheckedVersion != doc.Editor.Version && (DateTime.Now - doc.LastEdit).TotalSeconds > 0.4)
        {
            doc.CheckedVersion = doc.Editor.Version;
            string text = doc.Editor.Text, path = doc.Path, assets = AssetDatabase.assetsFolder;
            doc.CheckTask = Task.Run(() => ScriptAnalysis.Check(assets, path, text));
        }
    }

    private void Save(Document doc)
    {
        try
        {
            string text = doc.Editor.Text;
            File.WriteAllText(doc.Path, text.Replace("\n", Environment.NewLine));
            doc.SavedText = text;
            doc.SavedVersion = doc.Editor.Version;
            doc.DiskTime = File.GetLastWriteTimeUtc(doc.Path);
            Log.Info($"Saved {AssetDatabase.ToAssetPath(doc.Path)}");
        }
        catch (Exception e)
        {
            Log.Error($"Couldn't save {doc.Path}: {e.Message}");
        }
    }

    // If a file changed on disk (edited in VS Code, say) and has no unsaved
    // edits here, pick up the new text.
    private void CheckDiskChanges()
    {
        if ((DateTime.Now - _lastDiskCheck).TotalSeconds < 1) return;
        _lastDiskCheck = DateTime.Now;
        foreach (var doc in _docs.ToList())
        {
            if (!File.Exists(doc.Path)) continue;
            var time = File.GetLastWriteTimeUtc(doc.Path);
            if (time == doc.DiskTime || doc.Dirty) continue;
            doc.DiskTime = time;
            try
            {
                var cursor = doc.Editor.Cursor;
                doc.Editor.SetText(File.ReadAllText(doc.Path));
                doc.SavedText = doc.Editor.Text;
                doc.SavedVersion = doc.Editor.Version;
                doc.Editor.GoToLine(cursor.Line + 1, cursor.Col + 1);
            }
            catch (IOException) { /* still being written; try next time */ }
        }
    }

    private void DrawCloseConfirm()
    {
        const string id = "Unsaved changes";
        if (_confirmClose != null && !ImGui.IsPopupOpen(id)) ImGui.OpenPopup(id);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize)) return;
        var doc = _confirmClose;
        ImGui.TextUnformatted($"{System.IO.Path.GetFileName(doc?.Path)} has unsaved changes.");
        if (ImGui.Button("Save", new Vector2(100, 0)) && doc != null) { Save(doc); _docs.Remove(doc); _confirmClose = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Discard", new Vector2(100, 0)) && doc != null) { _docs.Remove(doc); _confirmClose = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0))) { _confirmClose = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
