namespace Engine.Core;

// The same attributes Unity uses to control what's saved and what the
// Inspector shows. The serialization rule is Unity's too:
//
//   public fields                 → saved + shown
//   private fields + [SerializeField] → saved + shown
//   [NonSerialized] (System's)    → neither
//   [HideInInspector]             → saved, but not shown
//
// Properties are never saved; that keeps "data" (fields) and "logic"
// (properties/methods) clearly separate.

[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeFieldAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field)]
public sealed class HideInInspectorAttribute : Attribute { }

// Shows a number field as a slider between min and max.
[AttributeUsage(AttributeTargets.Field)]
public sealed class RangeAttribute : Attribute
{
    public readonly float min, max;
    public RangeAttribute(float min, float max) { this.min = min; this.max = max; }
}

// A bold heading above a field, for grouping fields in the Inspector.
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public sealed class HeaderAttribute : Attribute
{
    public readonly string header;
    public HeaderAttribute(string header) => this.header = header;
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class TooltipAttribute : Attribute
{
    public readonly string tooltip;
    public TooltipAttribute(string tooltip) => this.tooltip = tooltip;
}

// Where a component appears in the Inspector's "Add Component" menu, e.g.
// "Rendering/Mesh Renderer". Components without one go under "Scripts".
[AttributeUsage(AttributeTargets.Class)]
public sealed class AddComponentMenuAttribute : Attribute
{
    public readonly string componentMenu;
    public AddComponentMenuAttribute(string menuName) => componentMenu = menuName;
}

// Adding this component also adds the listed ones if they're missing (a
// MeshRenderer is useless without a MeshFilter, for example).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireComponentAttribute : Attribute
{
    public readonly Type type;
    public RequireComponentAttribute(Type type) => this.type = type;
}

// Only one of this component per GameObject.
[AttributeUsage(AttributeTargets.Class)]
public sealed class DisallowMultipleComponentAttribute : Attribute { }
