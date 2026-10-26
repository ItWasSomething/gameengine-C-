using System.Collections;
using System.Numerics;
using Engine.Core;
using ImGuiNET;

namespace Editor.Panels;

// Unity's Inspector: the selected GameObject's header (active, name, tag,
// layer), its prefab status, and every component with its fields.
//
// It knows nothing about specific components. Fields come from
// TypeRegistry.GetSerializedFields (Unity's rule: public fields and
// [SerializeField] privates), and a widget is picked from each field's C#
// type. A brand-new script gets a working Inspector with zero UI code.
//
// Undo: widgets always edit a *copy* of the value. The moment a widget
// becomes active (click or drag start) an undo snapshot is recorded — while
// the component still holds the old value — and only then is the new value
// written back.
public sealed class InspectorPanel
{
    public const string Title = "INSPECTOR###Inspector";

    private readonly EditorContext _ctx;
    private string _nameBuffer = "";
    private GameObject? _nameOwner;
    private string _componentSearch = "";
    private Action? _deferred;

    // Set while drawing a component's fields, so OnValidate can be called
    // once after any of them changed.
    private bool _changedThisComponent;

    public InspectorPanel(EditorContext ctx) => _ctx = ctx;

    public void Draw()
    {
        if (!EditorTheme.BeginPanel(Title)) { ImGui.End(); return; }

        var go = _ctx.Selected;
        if (go == null || !go)
        {
            ImGui.TextDisabled("Select a GameObject to inspect it.");
            ImGui.End();
            return;
        }

        if (_ctx.IsPlaying)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            ImGui.TextWrapped("Playing: edits are live, but they're thrown away when you press Stop.");
            ImGui.PopStyleColor();
            ImGui.Separator();
        }

        DrawHeader(go);
        DrawPrefabBar(go);
        ImGui.Spacing();

        foreach (var component in go.components.ToList())
            DrawComponent(component);

        ImGui.Spacing();
        DrawAddComponentButton(go);

        _deferred?.Invoke();
        _deferred = null;
        ImGui.End();
    }

    // ---- GameObject header -------------------------------------------------------

    private void DrawHeader(GameObject go)
    {
        if (_nameOwner != go) { _nameBuffer = go.name; _nameOwner = go; }

        bool active = go.activeSelf;
        if (ImGui.Checkbox("##active", ref active))
        {
            _ctx.RecordUndo();
            go.SetActive(active);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Active: inactive objects (and their children) are hidden and don't run.");
        ImGui.SameLine();
        Widgets.GameObjectIcon(go, 16);
        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##name", ref _nameBuffer, 128);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _nameBuffer.Trim();
            if (trimmed.Length > 0 && trimmed != go.name)
            {
                _ctx.RecordUndo();
                go.name = trimmed;
            }
            _nameBuffer = go.name;
        }

        // Tag and Layer side by side.
        float half = (ImGui.GetContentRegionAvail().X - 8) / 2;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Tag");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(half - ImGui.CalcTextSize("Tag").X - 8);
        if (ImGui.BeginCombo("##tag", go.tag))
        {
            foreach (var tag in TagManager.allTags)
                if (ImGui.Selectable(tag, tag == go.tag))
                {
                    _ctx.RecordUndo();
                    go.tag = tag;
                }
            ImGui.Separator();
            if (ImGui.Selectable("Add Tag...")) _ctx.ShowTagsAndLayers = true;
            ImGui.EndCombo();
        }
        ImGui.SameLine(0, 8);
        ImGui.TextDisabled("Layer");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##layer", $"{go.layer}: {TagManager.LayerToName(go.layer)}"))
        {
            for (int i = 0; i < TagManager.LayerCount; i++)
            {
                string layerName = TagManager.layers[i];
                if (layerName.Length == 0) continue;
                if (ImGui.Selectable($"{i}: {layerName}", i == go.layer))
                {
                    _ctx.RecordUndo();
                    go.layer = i;
                }
            }
            ImGui.Separator();
            if (ImGui.Selectable("Add Layer...")) _ctx.ShowTagsAndLayers = true;
            ImGui.EndCombo();
        }
    }

    private void DrawPrefabBar(GameObject go)
    {
        var root = PrefabUtility.GetInstanceRoot(go);
        if (root == null) return;

        ImGui.Spacing();
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float height = ImGui.GetFrameHeight() + 8;
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height),
            Widgets.U32(EditorTheme.WithAlpha(Widgets.PrefabColor, 0.12f)), 5);
        ImGui.SetCursorScreenPos(start + new Vector2(6, 4));

        Widgets.PrefabIcon();
        ImGui.SameLine(0, 6);
        ImGui.AlignTextToFramePadding();
        string label = root == go ? Path.GetFileNameWithoutExtension(root.prefabAssetPath!) : $"in {root.name}";
        ImGui.TextColored(Widgets.PrefabColor, label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(root.prefabAssetPath);

        float buttons = ImGui.CalcTextSize("Select").X + ImGui.CalcTextSize("Revert").X + ImGui.CalcTextSize("Apply").X + 80;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + 8, ImGui.GetWindowContentRegionMax().X - buttons));
        if (ImGui.SmallButton("Select")) _ctx.PingAsset(root.prefabAssetPath!);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the prefab asset in the Project panel");
        ImGui.SameLine();
        if (ImGui.SmallButton("Revert")) _deferred = () => _ctx.RevertPrefab(root);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Throw away this instance's changes");
        ImGui.SameLine();
        if (ImGui.SmallButton("Apply")) _deferred = () => _ctx.ApplyPrefab(root);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Save this instance's changes into the prefab (updates every instance)");
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 2));
        ImGui.Dummy(Vector2.Zero);
    }

    // ---- Components ----------------------------------------------------------------

    private void DrawComponent(Component component)
    {
        var type = component.GetType();
        ImGui.PushID(component.instanceId.ToString());

        // Neutral warm surfaces for headers (the accent is kept for selection).
        ImGui.PushStyleColor(ImGuiCol.Header, EditorTheme.Bg2);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorTheme.Bg3);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorTheme.Bg3);
        bool open = ImGui.CollapsingHeader("##header", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
        ImGui.PopStyleColor(3);
        {
            // A stripe in the component's own color down the header's left edge.
            var hMin = ImGui.GetItemRectMin();
            var hMax = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddRectFilled(hMin, new Vector2(hMin.X + 3, hMax.Y), Widgets.U32(Widgets.ComponentColor(type)));
        }

        // Draw icon, enabled checkbox, title and menu button over the header.
        ImGui.SameLine(ImGui.GetTreeNodeToLabelSpacing() + 4);
        Widgets.ComponentIcon(type);
        ImGui.SameLine(0, 6);
        if (component is Behaviour behaviour)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
            bool enabled = behaviour.enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                _ctx.RecordUndo();
                behaviour.enabled = enabled;
            }
            ImGui.PopStyleVar();
            ImGui.SameLine(0, 6);
        }
        string title = component is MissingComponent missing ? $"Missing Script ({missing.scriptName})" : Widgets.NiceName(type.Name);
        ImGui.PushFont(EditorTheme.BoldFont);
        ImGui.TextColored(component is MissingComponent ? EditorTheme.Error : EditorTheme.Text, title);
        ImGui.PopFont();

        float menuWidth = ImGui.CalcTextSize("...").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - menuWidth - 2);
        if (ImGui.SmallButton("...")) ImGui.OpenPopup("componentMenu");
        ComponentMenu(component);

        if (open)
        {
            ImGui.Indent(6);
            _changedThisComponent = false;
            switch (component)
            {
                case Transform t: DrawTransform(t); break;
                case MissingComponent m:
                    ImGui.TextWrapped($"The script '{m.scriptName}' can't be found (deleted, renamed, or it doesn't compile). " +
                                      "Its saved values are kept — fix the script and they come back, or remove this component.");
                    break;
                default:
                    var fields = TypeRegistry.GetSerializedFields(type).Where(f => !f.HideInInspector).ToList();
                    if (fields.Count == 0) ImGui.TextDisabled("No editable fields.");
                    else if (BeginFieldTable("fields"))
                    {
                        DrawFields(component, fields);
                        ImGui.EndTable();
                    }
                    break;
            }
            if (_changedThisComponent) component.CallOnValidate();
            ImGui.Unindent(6);
            ImGui.Spacing();
        }
        ImGui.PopID();
    }

    private void ComponentMenu(Component component)
    {
        if (!ImGui.BeginPopup("componentMenu")) return;
        var go = component.gameObject;
        int index = go.components.ToList().IndexOf(component);
        bool isTransform = component is Transform;

        if (ImGui.MenuItem("Reset", null, false, component is not MissingComponent)) _deferred = () => _ctx.ResetComponent(component);
        string scriptTypeName = component is MissingComponent m ? m.scriptName : component.GetType().Name;
        if (_ctx.Scripts?.TypeFiles.TryGetValue(scriptTypeName, out var scriptFile) == true && ImGui.MenuItem("Edit Script"))
            _ctx.OpenScript?.Invoke(scriptFile!, null);
        ImGui.Separator();
        if (ImGui.MenuItem("Move Up", null, false, !isTransform && index > 1))
            _deferred = () => { _ctx.RecordUndo(); go.MoveComponent(component, index - 1); };
        if (ImGui.MenuItem("Move Down", null, false, !isTransform && index < go.components.Count - 1))
            _deferred = () => { _ctx.RecordUndo(); go.MoveComponent(component, index + 1); };
        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
        if (ImGui.MenuItem("Remove Component", null, false, !isTransform)) _deferred = () => _ctx.RemoveComponent(component);
        ImGui.PopStyleColor();
        ImGui.EndPopup();
    }

    private void DrawTransform(Transform t)
    {
        if (!BeginFieldTable("transform")) return;
        var p = t.localPosition;
        if (Row("Position", "Local position (relative to the parent)") && DragVector(ref p, 3, 0.05f)) t.localPosition = p;
        var r = t.localEulerAngles;
        if (Row("Rotation", "Local rotation in degrees (applied Z, then X, then Y)") && DragVector(ref r, 3, 0.5f)) t.localEulerAngles = r;
        var s = t.localScale;
        if (Row("Scale", "Local scale") && DragVector(ref s, 3, 0.02f)) t.localScale = s;
        ImGui.EndTable();
    }

    private static bool BeginFieldTable(string id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp)) return false;
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        return true;
    }

    // Starts a property row: label on the left, cursor left in the value
    // column ready for the widget. Always returns true (reads nicely in ifs).
    // Nesting depth of the fields being drawn (a Material inside a
    // MeshRenderer is depth 1); labels are indented by it.
    private static int _depth;

    private static bool Row(string label, string? tooltip = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + _depth * 14);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        return true;
    }

    // ---- Fields ------------------------------------------------------------------------

    private void DrawFields(object target, IEnumerable<SerializedField> fields)
    {
        foreach (var field in fields)
        {
            foreach (var header in field.Headers)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Spacing();
                ImGui.TextColored(EditorTheme.Accent, header);
            }

            ImGui.PushID(field.Name);
            object? value = field.GetValue(target);

            // A [Serializable] class/struct (e.g. a Material): a foldout row,
            // then its own fields as indented full-width rows below — not
            // squeezed into the value column.
            if (value != null && IsNestedObject(field.Type))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + _depth * 14);
                bool open = ImGui.TreeNodeEx(Widgets.NiceName(field.Name),
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);
                if (field.Tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(field.Tooltip);
                if (open)
                {
                    _depth++;
                    bool before = _changedThisComponent;
                    _changedThisComponent = false;
                    DrawFields(value, TypeRegistry.GetSerializedFields(field.Type).Where(f => !f.HideInInspector));
                    // Classes were edited in place; a struct is a copy and
                    // has to be written back.
                    if (_changedThisComponent && field.Type.IsValueType) field.SetValue(target, value);
                    _changedThisComponent |= before;
                    _depth--;
                }
                ImGui.PopID();
                continue;
            }

            Row(Widgets.NiceName(field.Name), field.Tooltip);
            if (DrawValue(ref value, field.Type, field))
            {
                field.SetValue(target, value);
                _changedThisComponent = true;
            }
            ImGui.PopID();
        }
    }

    private static bool IsNestedObject(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(Color) && type != typeof(LayerMask) &&
        type != typeof(Vector2) && type != typeof(Vector3) && type != typeof(Vector4) && type != typeof(Quaternion) &&
        !typeof(EngineObject).IsAssignableFrom(type) && !type.IsArray && !TypeRegistry.IsList(type, out _) &&
        TypeRegistry.IsSerializableType(type);

    // Draws one value's widget. Returns true when it changed (and `value`
    // holds the new value to write back).
    private bool DrawValue(ref object? value, Type type, SerializedField? meta)
    {
        var range = meta?.Range;
        bool changed = false;

        if (typeof(EngineObject).IsAssignableFrom(type))
            return ObjectField(ref value, type);

        if (type == typeof(LayerMask))
        {
            var mask = (LayerMask)(value ?? default(LayerMask));
            if (LayerMaskField(ref mask)) { value = mask; return true; }
            return false;
        }

        switch (value)
        {
            case float f:
                changed = range != null
                    ? ImGui.SliderFloat("##v", ref f, range.min, range.max, "%.3f")
                    : ImGui.DragFloat("##v", ref f, 0.05f, 0, 0, "%.3f");
                UndoOnActivate();
                value = f;
                return changed;

            case double d:
                float df = (float)d;
                changed = ImGui.DragFloat("##v", ref df, 0.05f);
                UndoOnActivate();
                value = (double)df;
                return changed;

            case int i:
                changed = range != null
                    ? ImGui.SliderInt("##v", ref i, (int)range.min, (int)range.max)
                    : ImGui.DragInt("##v", ref i, 0.2f);
                UndoOnActivate();
                value = i;
                return changed;

            case bool b:
                changed = ImGui.Checkbox("##v", ref b);
                UndoOnActivate();
                value = b;
                return changed;

            case string s:
                changed = ImGui.InputText("##v", ref s, 512);
                UndoOnActivate();
                value = s;
                return changed;

            case Enum e:
                var names = Enum.GetNames(e.GetType());
                int index = Array.IndexOf(names, e.ToString());
                if (ImGui.Combo("##v", ref index, names, names.Length))
                {
                    _ctx.RecordUndo();
                    value = Enum.Parse(e.GetType(), names[index]);
                    return true;
                }
                return false;

            case Vector2 v2:
                var v2as3 = new Vector3(v2, 0);
                changed = DragVector(ref v2as3, 2, 0.05f);
                value = new Vector2(v2as3.X, v2as3.Y);
                return changed;

            case Vector3 v3:
                changed = DragVector(ref v3, 3, 0.05f);
                value = v3;
                return changed;

            case Vector4 v4:
                changed = ImGui.DragFloat4("##v", ref v4, 0.05f);
                UndoOnActivate();
                value = v4;
                return changed;

            case Quaternion q:
                var euler = Transform.ToEulerDegrees(q);
                changed = DragVector(ref euler, 3, 0.5f);
                value = Transform.FromEulerDegrees(euler);
                return changed;

            case Color c:
                var c4 = c.ToVector4();
                changed = ImGui.ColorEdit4("##v", ref c4, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR |
                                                          ImGuiColorEditFlags.AlphaPreviewHalf);
                UndoOnActivate();
                value = new Color(c4.X, c4.Y, c4.Z, c4.W);
                return changed;

            case IList list:
                return ListField(ref value, list, type);

            case null when !type.IsValueType:
                // A [Serializable] class field that's null — create it on demand.
                if (ImGui.SmallButton("Create"))
                {
                    _ctx.RecordUndo();
                    value = TypeRegistry.DefaultValue(type);
                    return value != null;
                }
                return false;

            default:
                // A [Serializable] class or struct: its own fields, nested.
                if (value != null && TypeRegistry.IsSerializableType(type))
                {
                    bool open = ImGui.TreeNodeEx(Widgets.NiceName(type.Name), ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
                    if (!open) return false;
                    ImGui.TreePop();
                    bool nestedChanged = false;
                    if (BeginFieldTable("nested"))
                    {
                        bool before = _changedThisComponent;
                        _changedThisComponent = false;
                        DrawFields(value, TypeRegistry.GetSerializedFields(type).Where(f => !f.HideInInspector));
                        nestedChanged = _changedThisComponent;
                        _changedThisComponent = before || nestedChanged;
                        ImGui.EndTable();
                    }
                    // Classes were edited in place; structs are copies and
                    // must be written back by the caller.
                    return nestedChanged && type.IsValueType;
                }
                ImGui.TextDisabled(value?.ToString() ?? "null");
                return false;
        }
    }

    private void UndoOnActivate()
    {
        if (ImGui.IsItemActivated()) _ctx.RecordUndo();
    }

    // Lists and arrays: a "Size" field plus one row per element.
    private bool ListField(ref object? value, IList list, Type type)
    {
        var elementType = TypeRegistry.ElementType(type);
        bool open = ImGui.TreeNodeEx($"{list.Count} element{(list.Count == 1 ? "" : "s")}", ImGuiTreeNodeFlags.SpanAvailWidth);
        if (!open) return false;
        ImGui.TreePop();

        bool changed = false;
        int size = list.Count;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("Size", ref size, 1, 1, ImGuiInputTextFlags.EnterReturnsTrue) && size >= 0 && size != list.Count)
        {
            _ctx.RecordUndo();
            var resized = TypeRegistry.CreateCollection(type, size);
            for (int i = 0; i < Math.Min(size, list.Count); i++) resized[i] = list[i];
            value = resized;
            return true;
        }

        if (BeginFieldTable("elements"))
        {
            for (int i = 0; i < list.Count; i++)
            {
                Row($"Element {i}");
                ImGui.PushID(i);
                object? element = list[i];
                if (DrawValue(ref element, elementType, null))
                {
                    list[i] = element;
                    changed = true;
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        _changedThisComponent |= changed;
        return false;   // elements were edited in place
    }

    // A slot holding a reference to a GameObject or component. Drag one in
    // from the Hierarchy (or a prefab from the Project panel), or pick from
    // a list with the circle button.
    private bool ObjectField(ref object? value, Type type)
    {
        var current = value as EngineObject;
        string label = current ? $"{current!.name} ({current.GetType().Name})" : $"None ({type.Name})";

        float pickerWidth = ImGui.GetFrameHeight();
        float width = ImGui.GetContentRegionAvail().X - pickerWidth - 4;
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
        if (ImGui.Button(label + "##obj", new Vector2(width, 0)) && current is GameObject or Component)
        {
            var target = current is Component comp ? comp.gameObject : (GameObject)current!;
            if (target.scene == null && AssetDatabase.GetAssetPath(target) is { } assetPath) _ctx.PingAsset(assetPath);
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Drag a {type.Name} here from the Hierarchy or Project panel");

        bool changed = false;
        if (ImGui.BeginDragDropTarget())
        {
            if (DragPayload.Accept(DragPayload.GameObjectType) && DragPayload.GameObject is { } dropped &&
                Convert(dropped, type) is { } converted)
            {
                _ctx.RecordUndo();
                value = converted;
                changed = true;
            }
            else if (DragPayload.Accept(DragPayload.AssetType) && DragPayload.AssetPath is { } asset &&
                     asset.EndsWith(SceneSerializer.PrefabExtension) && AssetDatabase.LoadPrefab(asset) is { } prefab &&
                     Convert(prefab, type) is { } convertedPrefab)
            {
                _ctx.RecordUndo();
                value = convertedPrefab;
                changed = true;
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine(0, 4);
        if (ImGui.Button("o##pick", new Vector2(pickerWidth, 0))) ImGui.OpenPopup("picker");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick from a list");
        if (ImGui.BeginPopup("picker"))
        {
            if (ImGui.Selectable("None"))
            {
                _ctx.RecordUndo();
                value = null;
                changed = true;
            }
            ImGui.Separator();
            ImGui.TextDisabled("Scene");
            foreach (var go in _ctx.Scene.AllGameObjects())
                if (Convert(go, type) is { } candidate && ImGui.Selectable($"{go.name}##{go.instanceId}"))
                {
                    _ctx.RecordUndo();
                    value = candidate;
                    changed = true;
                }
            ImGui.Separator();
            ImGui.TextDisabled("Prefabs");
            foreach (var file in Directory.EnumerateFiles(AssetDatabase.assetsFolder, "*" + SceneSerializer.PrefabExtension, SearchOption.AllDirectories))
            {
                string assetPath = AssetDatabase.ToAssetPath(file);
                if (AssetDatabase.LoadPrefab(assetPath) is { } prefab && Convert(prefab, type) is { } candidate &&
                    ImGui.Selectable(assetPath))
                {
                    _ctx.RecordUndo();
                    value = candidate;
                    changed = true;
                }
            }
            ImGui.EndPopup();
        }
        return changed;
    }

    // A GameObject fits a GameObject field directly, or a component field
    // if it has that component.
    private static EngineObject? Convert(GameObject go, Type type) =>
        type == typeof(GameObject) || type == typeof(EngineObject) ? go : go.GetComponent(type);

    private bool LayerMaskField(ref LayerMask mask)
    {
        string preview = mask.value == 0 ? "Nothing" : mask.value == ~0 ? "Everything" :
            string.Join(", ", Enumerable.Range(0, TagManager.LayerCount).Where(mask.Contains).Select(TagManager.LayerToName).Where(n => n.Length > 0));
        bool changed = false;
        if (ImGui.BeginCombo("##mask", preview))
        {
            if (ImGui.Selectable("Nothing", mask.value == 0)) { _ctx.RecordUndo(); mask = 0; changed = true; }
            if (ImGui.Selectable("Everything", mask.value == ~0)) { _ctx.RecordUndo(); mask = ~0; changed = true; }
            ImGui.Separator();
            for (int i = 0; i < TagManager.LayerCount; i++)
            {
                string name = TagManager.layers[i];
                if (name.Length == 0) continue;
                bool on = mask.Contains(i);
                if (ImGui.Checkbox($"{i}: {name}", ref on))
                {
                    _ctx.RecordUndo();
                    mask = on ? mask.value | (1 << i) : mask.value & ~(1 << i);
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    // X/Y/Z drag fields side by side, each with a colored axis tab — the
    // same colors as the gizmo, so "red = X" everywhere.
    private bool DragVector(ref Vector3 v, int components, float speed)
    {
        bool changed = false;
        float full = ImGui.GetContentRegionAvail().X;
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        float width = (full - spacing * (components - 1)) / components;
        var colors = new[] { EditorTheme.AxisX, EditorTheme.AxisY, EditorTheme.AxisZ };
        var labels = new[] { "X", "Y", "Z" };

        for (int i = 0; i < components; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);
            ImGui.PushID(i);
            float component = i == 0 ? v.X : i == 1 ? v.Y : v.Z;

            var pos = ImGui.GetCursorScreenPos();
            float h = ImGui.GetFrameHeight();
            ImGui.SetNextItemWidth(width);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, ImGui.GetStyle().FramePadding.Y));
            if (ImGui.DragFloat("##c", ref component, speed, 0, 0, "%.3f"))
            {
                changed = true;
                if (i == 0) v.X = component; else if (i == 1) v.Y = component; else v.Z = component;
            }
            ImGui.PopStyleVar();
            UndoOnActivate();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(labels[i]);

            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(3, h),
                Widgets.U32(colors[i]));
            ImGui.PopID();
        }
        return changed;
    }

    // ---- Add Component -----------------------------------------------------------------

    private void DrawAddComponentButton(GameObject go)
    {
        float width = MathF.Min(260, ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) / 2);
        if (ImGui.Button("Add Component", new Vector2(width, 28)))
        {
            _componentSearch = "";
            ImGui.OpenPopup("AddComponent");
        }

        ImGui.SetNextWindowSize(new Vector2(width, 0));
        if (!ImGui.BeginPopup("AddComponent")) return;

        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search", ref _componentSearch, 64);
        ImGui.Separator();

        var types = TypeRegistry.AddableComponents.ToList();
        if (_componentSearch.Length > 0)
        {
            // Flat filtered list while searching.
            foreach (var t in types.Where(t => t.Name.Contains(_componentSearch, StringComparison.OrdinalIgnoreCase) ||
                                               TypeRegistry.MenuPath(t).Contains(_componentSearch, StringComparison.OrdinalIgnoreCase)))
                ComponentItem(go, t, TypeRegistry.MenuPath(t));
        }
        else
        {
            // Category submenus: "Rendering/Light" → Rendering ▸ Light.
            foreach (var group in types.GroupBy(t => TypeRegistry.MenuPath(t).Split('/')[0]))
            {
                if (!ImGui.BeginMenu(group.Key)) continue;
                foreach (var t in group)
                    ComponentItem(go, t, TypeRegistry.MenuPath(t).Split('/').Last());
                ImGui.EndMenu();
            }
        }

        // Unity's "New Script": type a name in the search box, then pick this.
        // The file is created in Assets/Scripts, compiled, and added here.
        ImGui.Separator();
        string scriptName = _componentSearch.Trim().Length > 0 ? _componentSearch.Trim().Replace(" ", "") : "NewBehaviour";
        bool exists = TypeRegistry.FindComponentType(scriptName) != null;
        if (_ctx.Scripts != null && !exists && ImGui.Selectable($"+ New Script \"{scriptName}\""))
        {
            if (_ctx.Scripts.CreateScript(scriptName, go) is { } file) _ctx.OpenScript?.Invoke(file, null);
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Type a name in the search box first");
        ImGui.EndPopup();
    }

    private void ComponentItem(GameObject go, Type type, string label)
    {
        Widgets.ComponentIcon(type, 13);
        ImGui.SameLine(0, 6);
        bool exists = go.GetComponent(type) != null;
        bool blocked = exists && Attribute.IsDefined(type, typeof(DisallowMultipleComponentAttribute));
        if (ImGui.Selectable(Widgets.NiceName(label), false, blocked ? ImGuiSelectableFlags.Disabled : 0))
        {
            _ctx.AddComponent(go, type);
            ImGui.CloseCurrentPopup();
        }
    }
}
