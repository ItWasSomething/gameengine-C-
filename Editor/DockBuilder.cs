using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Editor;

// ImGui's DockBuilder API lets code arrange docked windows (instead of the
// user dragging them into place). ImGui.NET doesn't wrap it because it lives
// in imgui_internal.h, but the native cimgui library it ships does export
// it — so we call those functions directly with P/Invoke.
//
// Used once, on first launch (no imgui.ini yet), to build the default
// Godot-like layout. After that, ImGui remembers whatever the user arranged.
internal static unsafe class DockBuilder
{
    private const string Lib = "cimgui";

    // ImGuiDockNodeFlags_DockSpace (internal flag): marks the root node as a
    // dockspace host.
    public const ImGuiDockNodeFlags DockSpaceFlag = (ImGuiDockNodeFlags)(1 << 10);

    [DllImport(Lib, EntryPoint = "igDockBuilderAddNode")]
    public static extern uint AddNode(uint nodeId, ImGuiDockNodeFlags flags);

    [DllImport(Lib, EntryPoint = "igDockBuilderRemoveNode")]
    public static extern void RemoveNode(uint nodeId);

    [DllImport(Lib, EntryPoint = "igDockBuilderSetNodeSize")]
    public static extern void SetNodeSize(uint nodeId, Vector2 size);

    [DllImport(Lib, EntryPoint = "igDockBuilderSplitNode")]
    public static extern uint SplitNode(uint nodeId, ImGuiDir direction, float sizeRatio, uint* outIdAtDir, uint* outIdOpposite);

    [DllImport(Lib, EntryPoint = "igDockBuilderDockWindow")]
    public static extern void DockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

    [DllImport(Lib, EntryPoint = "igDockBuilderGetNode")]
    public static extern IntPtr GetNode(uint nodeId);

    [DllImport(Lib, EntryPoint = "igDockBuilderFinish")]
    public static extern void Finish(uint nodeId);

    // Splits `node` and returns the new piece on side `dir`; `node` becomes
    // the remainder.
    public static uint Split(ref uint node, ImGuiDir dir, float ratio)
    {
        uint atDir, rest;
        SplitNode(node, dir, ratio, &atDir, &rest);
        node = rest;
        return atDir;
    }
}
