using System.Numerics;
using Engine;
using Engine.Core;
using ImGuiNET;

namespace Editor.Panels;

// Unity's Project window: the project's Assets/ folder as a tree.
//   - double-click a .scene to open it
//   - drag a .prefab into the Hierarchy or Scene view to place an instance
//   - drag a GameObject from the Hierarchy onto a folder to make a prefab
//   - right-click for Create (Folder, Scene), Show in Explorer, Delete
// The listing is cached and re-scanned every couple of seconds rather than
// hitting the disk every frame.
public sealed class ProjectPanel
{
    public const string Title = "PROJECT###Project";
    private const double RefreshSeconds = 2.0;

    private readonly EditorContext _ctx;
    private Folder? _root;
    private DateTime _lastRefresh = DateTime.MinValue;
    private string? _selectedFile;
    private string? _pendingDelete;
    private string? _createFolderIn;
    private string _newName = "";
    private enum NewAsset { Folder, Scene, Script }
    private NewAsset _newKind;

    private sealed record Folder(string Path, string Name, List<Folder> Folders, List<string> Files);

    public ProjectPanel(EditorContext ctx) => _ctx = ctx;

    public void Draw()
    {
        if (!EditorTheme.BeginPanel(Title)) { ImGui.End(); return; }

        if ((DateTime.Now - _lastRefresh).TotalSeconds > RefreshSeconds) Refresh();

        ImGui.TextDisabled(_selectedFile != null ? AssetDatabase.ToAssetPath(_selectedFile) : "Assets");
        ImGui.SameLine(ImGui.GetContentRegionMax().X - 60);
        if (ImGui.SmallButton("Refresh")) Refresh();
        ImGui.Separator();

        ImGui.BeginChild("files");
        if (_root != null) DrawFolder(_root, isRoot: true);

        // Background: drop target for "make prefab in Assets/" + context menu.
        var remaining = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##bg", new Vector2(MathF.Max(remaining.X, 1), MathF.Max(remaining.Y, 40)));
        FolderDropTarget(AssetDatabase.assetsFolder);
        FolderContextMenu(AssetDatabase.assetsFolder, "bgctx", isRoot: true);
        ImGui.EndChild();

        DrawDeleteConfirm();
        DrawCreateDialog();
        ImGui.End();
    }

    public void Refresh()
    {
        _lastRefresh = DateTime.Now;
        try
        {
            Directory.CreateDirectory(AssetDatabase.assetsFolder);
            _root = Scan(AssetDatabase.assetsFolder);
        }
        catch { _root = null; }
    }

    private static Folder Scan(string path)
    {
        var dirs = Directory.GetDirectories(path)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(Scan).ToList();
        var files = Directory.GetFiles(path)
            .Where(f => !f.EndsWith(".recovery" + SceneSerializer.SceneExtension))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        return new Folder(path, Path.GetFileName(path), dirs, files);
    }

    private bool IsPinged(string path) =>
        _ctx.PingedAsset != null && (DateTime.Now - _ctx.PingTime).TotalSeconds < 2.5 &&
        string.Equals(AssetDatabase.ToAbsolutePath(_ctx.PingedAsset), path, StringComparison.OrdinalIgnoreCase);

    private bool ContainsPinged(Folder folder) =>
        _ctx.PingedAsset != null && (DateTime.Now - _ctx.PingTime).TotalSeconds < 0.2 &&
        AssetDatabase.ToAbsolutePath(_ctx.PingedAsset).StartsWith(folder.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void DrawFolder(Folder folder, bool isRoot)
    {
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (isRoot) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (ContainsPinged(folder)) ImGui.SetNextItemOpen(true);

        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Rgb(0xE8C77A));
        bool open = ImGui.TreeNodeEx(folder.Path, flags, folder.Name);
        ImGui.PopStyleColor();
        FolderDropTarget(folder.Path);
        FolderContextMenu(folder.Path, folder.Path + "#ctx", isRoot);
        if (!isRoot && ImGui.BeginDragDropSource())
        {
            // Folders can't be dragged anywhere useful yet; swallow the drag.
            ImGui.TextUnformatted(folder.Name);
            ImGui.EndDragDropSource();
        }
        if (!open) return;

        foreach (var sub in folder.Folders) DrawFolder(sub, false);
        foreach (var file in folder.Files) DrawFile(file);
        ImGui.TreePop();
    }

    private void DrawFile(string file)
    {
        string name = Path.GetFileName(file);
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool isScene = ext == SceneSerializer.SceneExtension;
        bool isPrefab = ext == SceneSerializer.PrefabExtension;
        bool isOpenScene = string.Equals(file, _ctx.ScenePath, StringComparison.OrdinalIgnoreCase);
        bool pinged = IsPinged(file);
        if (pinged) ImGui.SetScrollHereY();

        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (file == _selectedFile || pinged) flags |= ImGuiTreeNodeFlags.Selected;

        ImGui.TreeNodeEx(file, flags, "");
        if (ImGui.IsItemClicked()) _selectedFile = file;

        if (ImGui.BeginDragDropSource())
        {
            DragPayload.AssetPath = AssetDatabase.ToAssetPath(file);
            ImGui.SetDragDropPayload(DragPayload.AssetType, IntPtr.Zero, 0);
            if (isPrefab) { Widgets.PrefabIcon(); ImGui.SameLine(0, 6); }
            ImGui.TextUnformatted(name);
            ImGui.EndDragDropSource();
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (isScene) _ctx.OpenScene(file);
            else if (isPrefab) Log.Info("Drag a prefab into the Hierarchy or Scene view to place it.");
            else if (ext == ".cs" && _ctx.OpenScript != null) _ctx.OpenScript(file, null);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }

        if (ImGui.BeginPopupContextItem(file + "#ctx"))
        {
            if (isScene && ImGui.MenuItem("Open")) _ctx.OpenScene(file);
            if (isPrefab && ImGui.MenuItem("Place in Scene")) _ctx.InstantiatePrefab(AssetDatabase.ToAssetPath(file));
            if (ext == ".cs" && ImGui.MenuItem("Open")) _ctx.OpenScript?.Invoke(file, null);
            if (ext == ".cs" && ImGui.MenuItem("Open in External Editor"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
            if (ImGui.MenuItem("Show in Explorer"))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            if (ImGui.MenuItem("Delete...")) _pendingDelete = file;
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        // Icon + name, drawn over the label-less tree row.
        ImGui.SameLine(0, 0);
        if (isPrefab) Widgets.PrefabIcon();
        else FileIcon(ext);
        ImGui.SameLine(0, 6);
        var color = isPrefab ? Widgets.PrefabBlue : isScene ? EditorTheme.Accent : ext == ".cs" ? EditorTheme.Rgb(0x8FD18F) : EditorTheme.Text;
        ImGui.TextColored(color, isOpenScene ? name + "  (open)" : name);
    }

    private static void FileIcon(string ext)
    {
        var pos = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight();
        var dl = ImGui.GetWindowDrawList();
        var color = Widgets.U32(ext == SceneSerializer.SceneExtension ? EditorTheme.Accent : EditorTheme.TextDim);
        var min = pos + new Vector2(2, 1);
        var max = pos + new Vector2(11, h - 1);
        if (ext == SceneSerializer.SceneExtension)
        {
            // A little "clapper": scene files.
            dl.AddRectFilled(min, max, color, 2);
            dl.AddLine(new Vector2(min.X, min.Y + 4), new Vector2(max.X, min.Y + 4), Widgets.U32(new Vector4(0, 0, 0, 0.6f)), 1.5f);
        }
        else dl.AddRect(min, max, color, 1.5f, ImDrawFlags.None, 1.2f);
        ImGui.Dummy(new Vector2(13, h));
    }

    private void FolderDropTarget(string folderPath)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        if (DragPayload.Accept(DragPayload.GameObjectType) && DragPayload.GameObject is { } go)
        {
            _ctx.CreatePrefab(go, folderPath);
            DragPayload.GameObject = null;
            Refresh();
        }
        ImGui.EndDragDropTarget();
    }

    private void FolderContextMenu(string folderPath, string id, bool isRoot)
    {
        if (!ImGui.BeginPopupContextItem(id)) return;
        if (ImGui.BeginMenu("Create"))
        {
            if (ImGui.MenuItem("Folder")) OpenCreateDialog(folderPath, NewAsset.Folder);
            if (ImGui.MenuItem("Scene")) OpenCreateDialog(folderPath, NewAsset.Scene);
            if (ImGui.MenuItem("C# Script")) OpenCreateDialog(folderPath, NewAsset.Script);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Show in Explorer")) System.Diagnostics.Process.Start("explorer.exe", $"\"{folderPath}\"");
        if (!isRoot)
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            if (ImGui.MenuItem("Delete...")) _pendingDelete = folderPath;
            ImGui.PopStyleColor();
        }
        ImGui.EndPopup();
    }

    private void OpenCreateDialog(string folder, NewAsset kind)
    {
        _createFolderIn = folder;
        _newKind = kind;
        _newName = kind switch { NewAsset.Scene => "New Scene", NewAsset.Script => "NewBehaviour", _ => "New Folder" };
    }

    private void DrawCreateDialog()
    {
        const string id = "Create Asset";
        if (_createFolderIn != null && !ImGui.IsPopupOpen(id)) ImGui.OpenPopup(id);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled(_newKind switch { NewAsset.Scene => "New scene file", NewAsset.Script => "New script (class name)", _ => "New folder" });
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(300);
        bool enter = ImGui.InputText("##name", ref _newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        string clean = string.Concat(_newName.Trim().Split(Path.GetInvalidFileNameChars()));
        if ((ImGui.Button("Create", new Vector2(120, 0)) || enter) && clean.Length > 0 && _createFolderIn != null)
        {
            if (_newKind == NewAsset.Script)
            {
                if (_ctx.Scripts?.CreateScript(clean, null, _createFolderIn) is { } script) _ctx.OpenScript?.Invoke(script, null);
            }
            else if (_newKind == NewAsset.Scene)
            {
                string file = Path.Combine(_createFolderIn, clean + SceneSerializer.SceneExtension);
                if (!File.Exists(file))
                    File.WriteAllText(file, $"{{ \"format\": {SceneSerializer.FormatVersion}, \"kind\": \"scene\", \"name\": \"{clean}\", \"roots\": [] }}");
            }
            else Directory.CreateDirectory(Path.Combine(_createFolderIn, clean));
            _createFolderIn = null;
            Refresh();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _createFolderIn = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // Deleting files can't be undone, so always ask first.
    private void DrawDeleteConfirm()
    {
        const string id = "Delete Asset?";
        if (_pendingDelete != null && !ImGui.IsPopupOpen(id)) ImGui.OpenPopup(id);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize)) return;

        string target = _pendingDelete ?? "";
        bool isDir = Directory.Exists(target);
        ImGui.TextUnformatted($"Delete {AssetDatabase.ToAssetPath(target)}{(isDir ? " and everything in it" : "")}?");
        ImGui.TextColored(EditorTheme.Warning, "This can't be undone.");
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, EditorTheme.WithAlpha(EditorTheme.Error, 0.6f));
        if (ImGui.Button("Delete", new Vector2(120, 0)))
        {
            try
            {
                if (isDir) Directory.Delete(target, recursive: true);
                else File.Delete(target);
                AssetDatabase.Invalidate(AssetDatabase.ToAssetPath(target));
                Log.Info($"Deleted {AssetDatabase.ToAssetPath(target)}");
            }
            catch (Exception e) { Log.Error($"Couldn't delete: {e.Message}"); }
            _pendingDelete = null;
            Refresh();
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _pendingDelete = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
