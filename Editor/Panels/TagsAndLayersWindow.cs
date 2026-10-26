using System.Numerics;
using Engine.Core;
using ImGuiNET;

namespace Editor.Panels;

// Edit > Project Settings > Tags and Layers. Changes are saved to
// ProjectSettings/TagManager.json as soon as they're made.
public sealed class TagsAndLayersWindow
{
    private readonly EditorContext _ctx;
    private string _newTag = "";

    public TagsAndLayersWindow(EditorContext ctx) => _ctx = ctx;

    public void Draw()
    {
        if (!_ctx.ShowTagsAndLayers) return;
        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f));
        if (EditorTheme.BeginPanel("TAGS AND LAYERS###Tags and Layers", ref open, ImGuiWindowFlags.NoDocking))
        {
            bool changed = false;

            if (ImGui.CollapsingHeader("Tags", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var tag in TagManager.BuiltinTags)
                    ImGui.TextDisabled($"  {tag}  (built-in)");
                foreach (var tag in TagManager.customTags.ToList())
                {
                    ImGui.PushID(tag);
                    ImGui.TextUnformatted($"  {tag}");
                    ImGui.SameLine(ImGui.GetContentRegionMax().X - 24);
                    if (ImGui.SmallButton("x"))
                    {
                        TagManager.customTags.Remove(tag);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove tag (objects using it keep the text)");
                    ImGui.PopID();
                }
                ImGui.SetNextItemWidth(-70);
                bool enter = ImGui.InputTextWithHint("##newtag", "New tag", ref _newTag, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                if ((ImGui.Button("Add") || enter) && _newTag.Trim().Length > 0)
                {
                    TagManager.AddTag(_newTag);
                    _newTag = "";
                    changed = true;
                }
            }

            if (ImGui.CollapsingHeader("Layers", ImGuiTreeNodeFlags.DefaultOpen) &&
                ImGui.BeginTable("layers", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("n", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
                for (int i = 0; i < TagManager.LayerCount; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    bool builtin = TagManager.IsBuiltinLayer(i);
                    ImGui.TextDisabled(builtin ? $"Builtin Layer {i}" : $"User Layer {i}");
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    string name = TagManager.layers[i];
                    if (builtin) ImGui.TextDisabled(name.Length > 0 ? name : "-");
                    else if (ImGui.InputText($"##layer{i}", ref name, 64))
                    {
                        TagManager.layers[i] = name;
                        changed = true;
                    }
                }
                ImGui.EndTable();
            }

            if (changed) TagManager.Save(_ctx.ProjectDirectory);
        }
        ImGui.End();
        if (!open) _ctx.ShowTagsAndLayers = false;
    }
}
