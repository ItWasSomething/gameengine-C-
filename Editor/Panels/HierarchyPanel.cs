using System.Numerics;
using Engine.Core;
using ImGuiNET;

namespace Editor.Panels;

// Unity's Hierarchy: every GameObject in the open scene as a tree.
//   - click to select, double-click / F2 to rename
//   - drag onto an object's middle → make it a child
//   - drag onto an object's top/bottom edge → put it before/after (same parent)
//   - drag onto the scene row or empty space → move to the scene root
//   - drag a .prefab in from the Project panel → instantiate it there
//   - right-click for create / rename / duplicate / delete / prefab actions
public sealed class HierarchyPanel
{
    public const string Title = "HIERARCHY###Hierarchy";

    private readonly EditorContext _ctx;
    private GameObject? _renaming;
    private string _renameBuffer = "";
    private string _filter = "";
    private GameObject? _lastSelected;
    private bool _revealSelection;

    // Structural changes are applied after drawing, never while iterating.
    private Action? _deferred;

    public HierarchyPanel(EditorContext ctx) => _ctx = ctx;

    public void Draw()
    {
        if (!EditorTheme.BeginPanel(Title)) { ImGui.End(); return; }

        if (Widgets.IconButton("##create", Icon.Add, false, "Create", EditorTheme.Accent, 24))
            ImGui.OpenPopup("CreateMenu");
        if (ImGui.BeginPopup("CreateMenu"))
        {
            CreateMenu.Items(_ctx, null);
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Search", ref _filter, 64);
        ImGui.Separator();

        ImGui.BeginChild("tree");
        _revealSelection = _ctx.Selected != _lastSelected;
        _lastSelected = _ctx.Selected;

        // The scene itself is the top row, like Unity.
        ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        bool sceneOpen = ImGui.TreeNodeEx("##scene", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding |
                                                     ImGuiTreeNodeFlags.DefaultOpen);
        DropTarget(null, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), forceChild: true);
        ImGui.SameLine(0, 4);
        ImGui.TextColored(EditorTheme.Text, _ctx.SceneDisplayName);
        if (sceneOpen)
        {
            foreach (var root in _ctx.Scene.GetRootGameObjects().ToList())
                DrawGameObject(root);
            ImGui.TreePop();
        }

        // Empty space below the tree: drop target for "move to root", and
        // click to deselect / right-click to create.
        var remaining = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##empty", new Vector2(MathF.Max(remaining.X, 1), MathF.Max(remaining.Y, 40)));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _ctx.Select(null);
        DropTarget(null, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), forceChild: true);
        if (ImGui.BeginPopupContextItem("EmptyContext"))
        {
            CreateMenu.Items(_ctx, null);
            ImGui.EndPopup();
        }
        ImGui.EndChild();

        _deferred?.Invoke();
        _deferred = null;
        ImGui.End();
    }

    private bool MatchesFilter(GameObject go) =>
        _filter.Length == 0 || go.name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        go.transform.Any(child => MatchesFilter(child.gameObject));

    private void DrawGameObject(GameObject go)
    {
        if (!MatchesFilter(go)) return;

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick |
                    ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
        if (go.transform.childCount == 0) flags |= ImGuiTreeNodeFlags.Leaf;
        if (go == _ctx.Selected) flags |= ImGuiTreeNodeFlags.Selected;
        if (_filter.Length > 0) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (_revealSelection && _ctx.Selected != null && _ctx.Selected != go && _ctx.Selected.transform.IsChildOf(go.transform))
            ImGui.SetNextItemOpen(true);

        ImGui.PushID(go.instanceId.ToString());
        bool open = ImGui.TreeNodeEx("##go", flags);
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = ImGui.GetItemRectMax();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen()) _ctx.Select(go);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _ctx.Select(go);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && go.transform.childCount == 0)
            BeginRename(go);
        if (_revealSelection && go == _ctx.Selected) ImGui.SetScrollHereY();

        if (ImGui.BeginDragDropSource())
        {
            DragPayload.GameObject = go;
            ImGui.SetDragDropPayload(DragPayload.GameObjectType, IntPtr.Zero, 0);
            Widgets.GameObjectIcon(go);
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(go.name);
            ImGui.EndDragDropSource();
        }
        DropTarget(go, rowMin, rowMax, forceChild: false);
        ContextMenu(go);

        // Icon + name on the same line as the label-less tree node.
        ImGui.SameLine(0, 2);
        Widgets.GameObjectIcon(go);
        ImGui.SameLine(0, 6);
        if (_renaming == go)
        {
            DrawRenameField(go);
        }
        else
        {
            var color = go.prefabAssetPath != null ? Widgets.PrefabBlue : EditorTheme.Text;
            if (!go.activeInHierarchy) color = EditorTheme.WithAlpha(color, 0.45f);
            ImGui.TextColored(color, go.name);
        }

        if (open)
        {
            foreach (var child in go.transform)
                DrawGameObject(child.gameObject);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    // `target` null = the scene root.
    private void DropTarget(GameObject? target, Vector2 rowMin, Vector2 rowMax, bool forceChild)
    {
        if (!ImGui.BeginDragDropTarget()) return;

        // Where on the row is the mouse? Top/bottom quarter = insert as a
        // sibling before/after; middle = become a child.
        float y = ImGui.GetMousePos().Y;
        float h = rowMax.Y - rowMin.Y;
        int zone = forceChild || target == null ? 0 : y < rowMin.Y + h * 0.25f ? -1 : y > rowMax.Y - h * 0.25f ? 1 : 0;

        // Draw the drop hint ourselves: a line for before/after, a box for "into".
        var dl = ImGui.GetWindowDrawList();
        var accent = Widgets.U32(EditorTheme.Accent);
        if (zone == 0) dl.AddRect(rowMin, rowMax, accent, 3, ImDrawFlags.None, 1.5f);
        else
        {
            float lineY = zone < 0 ? rowMin.Y : rowMax.Y;
            dl.AddLine(new Vector2(rowMin.X, lineY), new Vector2(rowMax.X, lineY), accent, 2);
        }

        if (DragPayload.Accept(DragPayload.GameObjectType) && DragPayload.GameObject is { } dragged)
        {
            var parent = target == null ? null : zone == 0 ? target.transform : target.transform.parent;
            int index = target == null || zone == 0 ? -1 : target.transform.GetSiblingIndex() + (zone > 0 ? 1 : 0);
            if (target != dragged)
            {
                _deferred = () =>
                {
                    // Moving down within the same parent: removing the
                    // dragged object first shifts the target index by one.
                    if (index >= 0 && dragged.transform.parent == parent && dragged.transform.GetSiblingIndex() < index) index--;
                    _ctx.Reparent(dragged, parent, index);
                };
            }
            DragPayload.GameObject = null;
        }
        else if (DragPayload.Accept(DragPayload.AssetType) && DragPayload.AssetPath is { } asset &&
                 asset.EndsWith(SceneSerializer.PrefabExtension, StringComparison.OrdinalIgnoreCase))
        {
            var parent = target == null ? null : zone == 0 ? target.transform : target.transform.parent;
            _deferred = () => _ctx.InstantiatePrefab(asset, parent);
            DragPayload.AssetPath = null;
        }
        ImGui.EndDragDropTarget();
    }

    private void ContextMenu(GameObject go)
    {
        if (!ImGui.BeginPopupContextItem("ctx")) return;

        if (ImGui.BeginMenu("Create Child"))
        {
            CreateMenu.Items(_ctx, go.transform);
            ImGui.EndMenu();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Rename", "F2")) BeginRename(go);
        if (ImGui.MenuItem("Duplicate", "Ctrl+D")) { _ctx.Select(go); _deferred = _ctx.DuplicateSelected; }
        if (ImGui.MenuItem(go.activeSelf ? "Deactivate" : "Activate"))
        {
            _ctx.RecordUndo();
            go.SetActive(!go.activeSelf);
        }

        if (PrefabUtility.GetInstanceRoot(go) is { } prefabRoot)
        {
            ImGui.Separator();
            if (ImGui.BeginMenu("Prefab"))
            {
                if (ImGui.MenuItem("Select Asset")) _ctx.PingAsset(prefabRoot.prefabAssetPath!);
                if (ImGui.MenuItem("Apply to Prefab")) _deferred = () => _ctx.ApplyPrefab(prefabRoot);
                if (ImGui.MenuItem("Revert Changes")) _deferred = () => _ctx.RevertPrefab(prefabRoot);
                if (ImGui.MenuItem("Unpack")) _ctx.UnpackPrefab(prefabRoot);
                ImGui.EndMenu();
            }
        }

        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
        if (ImGui.MenuItem("Delete", "Del")) { _ctx.Select(go); _deferred = _ctx.DeleteSelected; }
        ImGui.PopStyleColor();
        ImGui.EndPopup();
    }

    public void BeginRename(GameObject go)
    {
        _renaming = go;
        _renameBuffer = go.name;
    }

    private void DrawRenameField(GameObject go)
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing() || !ImGui.IsAnyItemActive()) ImGui.SetKeyboardFocusHere();
        bool done = ImGui.InputText("##rename", ref _renameBuffer, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        if (done && _renameBuffer.Trim().Length > 0 && _renameBuffer != go.name)
        {
            _ctx.RecordUndo();
            go.name = _renameBuffer.Trim();
        }
        if (done || ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsItemDeactivated())
            _renaming = null;
    }
}

// The "Create" menu (Unity's GameObject menu), shared by the Hierarchy's
// "+" button, its right-click menus, and the main menu bar.
public static class CreateMenu
{
    public static void Items(EditorContext ctx, Transform? parent)
    {
        if (ImGui.MenuItem("Create Empty")) ctx.CreateGameObject("Empty", parent);
        if (ImGui.BeginMenu("3D Object"))
        {
            foreach (var shape in Enum.GetNames<PrimitiveType>())
                if (ImGui.MenuItem(shape)) ctx.CreateGameObject(shape, parent);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Light"))
        {
            if (ImGui.MenuItem("Directional Light")) ctx.CreateGameObject("Directional Light", parent);
            if (ImGui.MenuItem("Point Light")) ctx.CreateGameObject("Point Light", parent);
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Camera")) ctx.CreateGameObject("Camera", parent);
        if (ImGui.MenuItem("Sky Environment")) ctx.CreateGameObject("Sky Environment", parent);
    }
}
